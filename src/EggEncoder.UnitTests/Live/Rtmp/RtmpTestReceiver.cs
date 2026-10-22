using System.Buffers;
using System.Net;
using System.Net.Sockets;
using EggEncoder.Live.Rtmp;

namespace EggEncoder.UnitTests.Live.Rtmp
{
    /// <summary>
    /// A test-only, single-connection RTMP receiver built on the item 3 primitives, just enough for a
    /// publisher (ffmpeg) to get through connect/createStream/publish and send its media. Records every
    /// byte the client sent (so a capture can be checked in) and every message it reassembled.
    /// Item 4's RtmpServer replaces it for real ingest.
    /// </summary>
    internal sealed class RtmpTestReceiver : IDisposable
    {
        private const uint PublishStreamId = 1;

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public RtmpTestReceiver()
        {
            _listener.Start();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public List<RtmpMessage> Messages { get; } = [];

        public MemoryStream ClientBytes { get; } = new();

        public Task RunAsync(TimeSpan timeout) => Task.Run(() =>
        {
            using var client = _listener.AcceptTcpClient();
            client.ReceiveTimeout = (int)timeout.TotalMilliseconds;
            var network = client.GetStream();

            var handshake = RtmpHandshake.CreateServer();
            network.Write(handshake.Start());
            while (!handshake.IsComplete)
            {
                var peerBytes = new byte[handshake.BytesExpected];
                network.ReadExactly(peerBytes);
                ClientBytes.Write(peerBytes);
                network.Write(handshake.Process(peerBytes));
            }

            var writer = new RtmpChunkWriter();
            var output = new ArrayBufferWriter<byte>();
            var done = false;
            var reader = new RtmpChunkReader(message =>
            {
                Messages.Add(message);
                if (message.Type != RtmpMessageType.CommandAmf0)
                {
                    return;
                }

                var command = RtmpCommand.Decode(message.Payload.Span);
                switch (command.Name)
                {
                    case "connect":
                        writer.Write(output, RtmpProtocolControl.ChunkStreamId, RtmpProtocolControl.CreateWindowAcknowledgementSize(2_500_000));
                        writer.Write(output, RtmpProtocolControl.ChunkStreamId, RtmpProtocolControl.CreateSetPeerBandwidth(2_500_000, RtmpPeerBandwidthLimitType.Dynamic));
                        writer.WriteSetChunkSize(output, 4096);
                        writer.Write(output, RtmpCommand.ChunkStreamId, RtmpCommand.ConnectResult(command.TransactionId).ToMessage());
                        break;
                    case "createStream":
                        writer.Write(output, RtmpCommand.ChunkStreamId, RtmpCommand.CreateStreamResult(command.TransactionId, PublishStreamId).ToMessage());
                        break;
                    case "publish":
                        writer.Write(output, RtmpProtocolControl.ChunkStreamId, RtmpUserControl.StreamBegin(PublishStreamId).ToMessage());
                        writer.Write(output, 5, RtmpCommand.OnStatus(RtmpStatusCodes.LevelStatus, RtmpStatusCodes.PublishStart, "Publishing.").ToMessage(PublishStreamId));
                        break;
                    case "deleteStream":
                        done = true;
                        break;
                    default:
                        if (command.TransactionId > 0)
                        {
                            writer.Write(output, RtmpCommand.ChunkStreamId, RtmpCommand.Result(command.TransactionId, null, Amf0Undefined).ToMessage());
                        }

                        break;
                }
            });

            var buffer = new byte[16 * 1024];
            while (!done)
            {
                var read = network.Read(buffer);
                if (read == 0)
                {
                    break;
                }

                ClientBytes.Write(buffer, 0, read);
                reader.Feed(buffer.AsSpan(0, read));
                if (reader.TryTakeAcknowledgement(out var sequence))
                {
                    writer.Write(output, RtmpProtocolControl.ChunkStreamId, RtmpProtocolControl.CreateAcknowledgement(sequence));
                }

                if (output.WrittenCount > 0)
                {
                    network.Write(output.WrittenSpan);
                    output.ResetWrittenCount();
                }
            }
        });

        private static EggEncoder.Live.Rtmp.Amf0.Amf0Value Amf0Undefined => EggEncoder.Live.Rtmp.Amf0.Amf0Value.Undefined;

        public void Dispose() => _listener.Stop();
    }
}
