using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using EggEncoder.Live.Rtmp;

namespace EggEncoder.UnitTests.Live.Rtmp
{
    /// <summary>
    /// A test-only RTMP publisher built on the item 3 primitives, driving <see cref="RtmpServer"/>
    /// one step at a time (handshake, connect, createStream, publish, media, unpublish) so tests can
    /// stop at any point or send something malformed instead. Item 6's RtmpPublishClient replaces it
    /// once merged.
    /// </summary>
    internal sealed class RtmpTestPublisher : IAsyncDisposable
    {
        private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(10);

        private readonly TcpClient _client;
        private readonly RtmpChunkWriter _writer = new();
        private readonly ArrayBufferWriter<byte> _output = new();
        private readonly RtmpChunkReader _reader;
        private readonly byte[] _buffer = new byte[64 * 1024];
        private double _nextTransactionId = 1;

        private RtmpTestPublisher(TcpClient client, Stream stream)
        {
            _client = client;
            Stream = stream;
            _reader = new RtmpChunkReader(Received.Add);
        }

        public Stream Stream { get; }

        public List<RtmpMessage> Received { get; } = [];

        public IEnumerable<RtmpCommand> ReceivedCommands =>
            Received.Where(m => m.Type == RtmpMessageType.CommandAmf0).Select(m => RtmpCommand.Decode(m.Payload.Span));

        public static async Task<RtmpTestPublisher> OpenAsync(IPEndPoint endPoint, bool tls = false)
        {
            var client = new TcpClient(endPoint.AddressFamily);
            await client.ConnectAsync(endPoint.Address, endPoint.Port);
            Stream stream = client.GetStream();
            if (tls)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false, (_, _, _, _) => true);
                await ssl.AuthenticateAsClientAsync("localhost");
                stream = ssl;
            }

            return new RtmpTestPublisher(client, stream);
        }

        /// <summary>Handshake, connect to <paramref name="app"/>, createStream, publish <paramref name="key"/>; returns the onStatus reply.</summary>
        public static async Task<(RtmpTestPublisher Publisher, RtmpCommand Status)> PublishAsync(
            IPEndPoint endPoint, string app = "live", string key = "streamkey", bool tls = false)
        {
            var publisher = await OpenAsync(endPoint, tls);
            await publisher.HandshakeAsync();
            await publisher.ConnectAsync(app);
            var streamId = await publisher.CreateStreamAsync();
            var status = await publisher.PublishAsync(streamId, key);
            return (publisher, status);
        }

        public uint StreamId { get; private set; }

        public async Task HandshakeAsync()
        {
            var handshake = RtmpHandshake.CreateClient();
            await Stream.WriteAsync(handshake.Start());
            while (!handshake.IsComplete)
            {
                var peer = new byte[handshake.BytesExpected];
                await Stream.ReadExactlyAsync(peer);
                await Stream.WriteAsync(handshake.Process(peer));
            }
        }

        public async Task<RtmpCommand> ConnectAsync(string app)
        {
            _writer.WriteSetChunkSize(_output, 4096);
            var transactionId = _nextTransactionId++;
            _writer.Write(_output, RtmpCommand.ChunkStreamId, RtmpCommand.Connect(transactionId, app, $"rtmp://127.0.0.1/{app}").ToMessage());
            await FlushAsync();
            return await WaitForReplyAsync(transactionId);
        }

        public async Task<uint> CreateStreamAsync()
        {
            var transactionId = _nextTransactionId++;
            await SendCommandAsync(RtmpCommand.CreateStream(transactionId));
            var reply = await WaitForReplyAsync(transactionId);
            return (uint)reply.GetNumberArgument(0)!.Value;
        }

        public async Task<RtmpCommand> PublishAsync(uint streamId, string key)
        {
            StreamId = streamId;
            await SendCommandAsync(RtmpCommand.ReleaseStream(_nextTransactionId++, key));
            await SendCommandAsync(RtmpCommand.FCPublish(_nextTransactionId++, key));
            await SendCommandAsync(RtmpCommand.Publish(key), streamId, chunkStreamId: 8);
            return await WaitForAsync(() => ReceivedCommands.FirstOrDefault(c => c.Name == "onStatus"));
        }

        public async Task SendCommandAsync(RtmpCommand command, uint streamId = 0, int chunkStreamId = RtmpCommand.ChunkStreamId)
        {
            _writer.Write(_output, chunkStreamId, command.ToMessage(streamId));
            await FlushAsync();
        }

        public async Task SendAsync(RtmpMessage message, int chunkStreamId = 6)
        {
            _writer.Write(_output, chunkStreamId, message);
            await FlushAsync();
        }

        public async Task SendRawAsync(byte[] bytes) => await Stream.WriteAsync(bytes);

        /// <summary>Sends every tag of an FLV file as an RTMP message on the publish stream (script tags as AMF0 data).</summary>
        public async Task SendFlvAsync(byte[] flv)
        {
            foreach (var (type, timestamp, body) in ReadFlvTags(flv))
            {
                var chunkStreamId = type == 8 ? 4 : type == 9 ? 6 : 5;
                _writer.Write(_output, chunkStreamId, new RtmpMessage((RtmpMessageType)type, StreamId, timestamp, body));
            }

            await FlushAsync();
        }

        public async Task UnpublishAsync(string key = "streamkey")
        {
            await SendCommandAsync(RtmpCommand.FCUnpublish(_nextTransactionId++, key));
            await SendCommandAsync(RtmpCommand.DeleteStream(_nextTransactionId++, StreamId));
        }

        public async Task<RtmpCommand> WaitForReplyAsync(double transactionId) =>
            await WaitForAsync(() => ReceivedCommands.FirstOrDefault(c => c.TransactionId == transactionId && c.Name is "_result" or "_error"));

        public async Task<T> WaitForAsync<T>(Func<T?> find)
            where T : class
        {
            using var timeout = new CancellationTokenSource(ReplyTimeout);
            while (true)
            {
                if (find() is { } found)
                {
                    return found;
                }

                var read = await Stream.ReadAsync(_buffer, timeout.Token);
                if (read == 0)
                {
                    throw new EndOfStreamException("The server closed the connection before the expected reply");
                }

                _reader.Feed(_buffer.AsSpan(0, read));
            }
        }

        /// <summary>Reads (and parses, when possible) until the server closes the connection; false if it is still open after <paramref name="timeout"/>.</summary>
        public async Task<bool> WaitForCloseAsync(TimeSpan? timeout = null)
        {
            using var cancel = new CancellationTokenSource(timeout ?? ReplyTimeout);
            var parse = true;
            try
            {
                while (true)
                {
                    var read = await Stream.ReadAsync(_buffer, cancel.Token);
                    if (read == 0)
                    {
                        return true;
                    }

                    if (parse)
                    {
                        try
                        {
                            _reader.Feed(_buffer.AsSpan(0, read));
                        }
                        catch (Exception e) when (e is RtmpProtocolException or InvalidOperationException)
                        {
                            parse = false;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (IOException)
            {
                return true;
            }
        }

        internal static IEnumerable<(byte Type, uint Timestamp, byte[] Body)> ReadFlvTags(byte[] flv)
        {
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(flv.AsSpan(5)) + 4;
            while (offset + 11 <= flv.Length)
            {
                var type = (byte)(flv[offset] & 0x1F);
                var size = flv[offset + 1] << 16 | flv[offset + 2] << 8 | flv[offset + 3];
                var timestamp = (uint)(flv[offset + 7] << 24 | flv[offset + 4] << 16 | flv[offset + 5] << 8 | flv[offset + 6]);
                yield return (type, timestamp, flv.AsSpan(offset + 11, size).ToArray());
                offset += 11 + size + 4;
            }
        }

        private async Task FlushAsync()
        {
            await Stream.WriteAsync(_output.WrittenMemory);
            _output.ResetWrittenCount();
        }

        public async ValueTask DisposeAsync()
        {
            await Stream.DisposeAsync();
            _client.Dispose();
        }
    }
}
