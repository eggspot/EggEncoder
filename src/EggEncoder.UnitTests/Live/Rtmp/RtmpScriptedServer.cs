using System.Buffers;
using System.Net;
using System.Net.Sockets;
using EggEncoder.Live.Rtmp;
using EggEncoder.Live.Rtmp.Amf0;

namespace EggEncoder.UnitTests.Live.Rtmp
{
    /// <summary>
    /// A test-only RTMP server for one connection, driven step by step so a test can answer the
    /// client's publish flow normally, wrongly, or not at all, and see every message the client sends.
    /// </summary>
    internal sealed class RtmpScriptedServer : IAsyncDisposable
    {
        private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(90);

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly RtmpChunkWriter _writer = new();
        private readonly ArrayBufferWriter<byte> _output = new();
        private readonly List<RtmpMessage> _received = [];
        private readonly List<RtmpMessage> _unmatched = [];
        private readonly byte[] _buffer = new byte[64 * 1024];
        private readonly RtmpChunkReader _reader;
        private TcpClient? _client;
        private Stream? _stream;

        public RtmpScriptedServer()
        {
            _reader = new RtmpChunkReader(message =>
            {
                _received.Add(message);
                _unmatched.Add(message);
            });
            _listener.Start();
        }

        public Uri Url(string app = "live") => new($"rtmp://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/{app}");

        public IReadOnlyList<RtmpMessage> Received => _received;

        public long BytesReceived => _reader.BytesReceived;

        public async Task AcceptAsync(bool handshake = true)
        {
            _client = await _listener.AcceptTcpClientAsync().WaitAsync(ReplyTimeout);
            _stream = _client.GetStream();
            if (!handshake)
            {
                return;
            }

            var server = RtmpHandshake.CreateServer();
            server.Start();
            while (!server.IsComplete)
            {
                var peer = new byte[server.BytesExpected];
                await _stream.ReadExactlyAsync(peer).AsTask().WaitAsync(ReplyTimeout);
                var reply = server.Process(peer);
                if (reply.Length > 0)
                {
                    await _stream.WriteAsync(reply);
                }
            }
        }

        public async Task<RtmpCommand> ExpectCommandAsync(string name)
        {
            var message = await ExpectAsync(m => m.Type == RtmpMessageType.CommandAmf0 && RtmpCommand.Decode(m.Payload.Span).Name == name);
            return RtmpCommand.Decode(message.Payload.Span);
        }

        public async Task<RtmpMessage> ExpectAsync(Func<RtmpMessage, bool> match)
        {
            using var timeout = new CancellationTokenSource(ReplyTimeout);
            while (true)
            {
                var index = _unmatched.FindIndex(m => match(m));
                if (index >= 0)
                {
                    var found = _unmatched[index];
                    _unmatched.RemoveAt(index);
                    return found;
                }

                var read = await _stream!.ReadAsync(_buffer, timeout.Token);
                if (read == 0)
                {
                    throw new EndOfStreamException("The client closed the connection before the expected message");
                }

                _reader.Feed(_buffer.AsSpan(0, read));
            }
        }

        public async Task<bool> WaitForCloseAsync()
        {
            using var timeout = new CancellationTokenSource(ReplyTimeout);
            try
            {
                while (await _stream!.ReadAsync(_buffer, timeout.Token) > 0)
                {
                }

                return true;
            }
            catch (IOException)
            {
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        public async Task<uint> AcceptPublishAsync()
        {
            var connect = await ExpectCommandAsync("connect");
            await SendAsync(RtmpCommand.ConnectResult(connect.TransactionId).ToMessage());
            var createStream = await ExpectCommandAsync("createStream");
            await SendAsync(RtmpCommand.CreateStreamResult(createStream.TransactionId, 1).ToMessage());
            await ExpectCommandAsync("publish");
            await SendStatusAsync(1, RtmpStatusCodes.LevelStatus, RtmpStatusCodes.PublishStart);
            await ExpectAsync(m => m.Type == RtmpMessageType.DataAmf0);
            return 1;
        }

        public Task SendStatusAsync(uint streamId, string level, string code) =>
            SendAsync(RtmpCommand.OnStatus(level, code, "scripted").ToMessage(streamId), chunkStreamId: 5);

        public Task SendErrorAsync(double transactionId, string code) =>
            SendAsync(RtmpCommand.Error(transactionId, code, "scripted").ToMessage());

        public Task SendResultAsync(double transactionId, params Amf0Value[] arguments) =>
            SendAsync(RtmpCommand.Result(transactionId, null, arguments).ToMessage());

        public async Task SendAsync(RtmpMessage message, int chunkStreamId = RtmpCommand.ChunkStreamId)
        {
            _writer.Write(_output, chunkStreamId, message);
            await _stream!.WriteAsync(_output.WrittenMemory);
            _output.ResetWrittenCount();
        }

        public async Task SendRawAsync(byte[] bytes) => await _stream!.WriteAsync(bytes);

        public void ResetConnection()
        {
            _client!.Client.LingerState = new LingerOption(true, 0);
            CloseConnection();
        }

        public void CloseConnection()
        {
            _stream?.Dispose();
            _client?.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            CloseConnection();
            _listener.Stop();
            _listener.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
