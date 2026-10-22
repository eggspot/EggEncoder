using EggEncoder.Containers.Flv;
using EggEncoder.Live.Rtmp;
using EggEncoder.UnitTests.Containers.Flv;
using FluentAssertions;

namespace EggEncoder.UnitTests.Live.Rtmp
{
    public class RtmpFfmpegCaptureTest
    {
        // Every byte ffmpeg 9.0.2 sent when publishing Containers/Flv/h264_aac.flv with
        // `ffmpeg -i h264_aac.flv -c copy -f flv rtmp://127.0.0.1:<port>/live/streamkey` to
        // RtmpTestReceiver: C0+C1, C2, then the chunk stream (captured via RtmpFfmpegOracleTest).
        internal static readonly string Fixture = Path.GetFullPath("Live/Rtmp/ffmpeg_publish.bin");

        private const int HandshakeLength = 1 + RtmpHandshake.PacketLength + RtmpHandshake.PacketLength;

        internal static List<RtmpMessage> ReadCapture(int feedSize)
        {
            var bytes = File.ReadAllBytes(Fixture);
            var handshake = RtmpHandshake.CreateServer();
            handshake.Start();
            handshake.Process(bytes.AsSpan(0, 1 + RtmpHandshake.PacketLength)).Should().HaveCount(1 + (2 * RtmpHandshake.PacketLength));
            handshake.Process(bytes.AsSpan(1 + RtmpHandshake.PacketLength, RtmpHandshake.PacketLength)).Should().BeEmpty();
            handshake.IsComplete.Should().BeTrue();

            var messages = new List<RtmpMessage>();
            var reader = new RtmpChunkReader(messages.Add);
            for (var offset = HandshakeLength; offset < bytes.Length; offset += Math.Min(feedSize, bytes.Length - offset))
            {
                reader.Feed(bytes.AsSpan(offset, Math.Min(feedSize, bytes.Length - offset)));
            }

            reader.ChunkSize.Should().Be(4096);
            return messages;
        }

        [Theory]
        [InlineData(1)]
        [InlineData(7)]
        [InlineData(1500)]
        [InlineData(int.MaxValue)]
        public void Capture_FromRealFfmpegPublish_Should_Parse_Every_Command_And_Tag(int feedSize)
        {
            var messages = ReadCapture(feedSize);

            AssertPublishSession(messages);
            messages[0].Type.Should().Be(RtmpMessageType.CommandAmf0, "ffmpeg sends connect first");
            messages[1].Type.Should().Be(RtmpMessageType.SetChunkSize);
            RtmpProtocolControl.ReadSetChunkSize(messages[1].Payload.Span).Should().Be(4096);
        }

        [Fact]
        public void Capture_ConnectCommand_Should_Span_Two_Chunks_At_The_Default_Chunk_Size()
        {
            // ffmpeg's connect is sent before its Set Chunk Size, so at 128 bytes per chunk it needs
            // a type 3 continuation: the case most likely to go wrong in a real handshake.
            var bytes = File.ReadAllBytes(Fixture);
            var connect = ReadCapture(int.MaxValue).First(m => m.Type == RtmpMessageType.CommandAmf0);

            connect.Payload.Length.Should().BeGreaterThan(RtmpChunkReader.DefaultChunkSize);
            bytes[HandshakeLength + 12 + RtmpChunkReader.DefaultChunkSize].Should().Be(0xC3);
            RtmpCommand.Decode(connect.Payload.Span).CommandObject.GetProperty("flashVer")!.String.Should().StartWith("FMLE/3.0 (compatible; Lavf");
        }

        internal static void AssertPublishSession(IReadOnlyList<RtmpMessage> messages)
        {
            var commands = messages.Where(m => m.Type == RtmpMessageType.CommandAmf0).Select(m => RtmpCommand.Decode(m.Payload.Span)).ToList();
            commands.Select(c => c.Name).Should().ContainInOrder("connect", "releaseStream", "FCPublish", "createStream", "publish");
            var connect = commands[0];
            connect.CommandObject.GetProperty("app")!.String.Should().Be("live");
            connect.CommandObject.GetProperty("tcUrl")!.String.Should().Be($"rtmp://127.0.0.1:{TcUrlPort(connect)}/live");
            commands.Single(c => c.Name == "publish").GetStringArgument(0).Should().Be("streamkey");
            commands.Single(c => c.Name == "publish").GetStringArgument(1).Should().Be("live");

            var metadata = messages.Where(m => m.Type == RtmpMessageType.DataAmf0)
                .Select(m => RtmpDataMessage.Decode(m.Payload.Span).TryGetMetadata())
                .Single(m => m is not null)!;
            metadata.Width.Should().Be(64);
            metadata.Height.Should().Be(64);

            // Every audio/video message is an FLV tag body, so re-wrapping them as FLV tags must demux
            // to exactly what the source file demuxes to.
            var media = messages.Where(m => m.Type is RtmpMessageType.Audio or RtmpMessageType.Video).ToList();
            media.Should().OnlyContain(m => m.StreamId == 1);
            var original = FlvDemuxerTest.Demux(File.ReadAllBytes(FlvDemuxerTest.H264AacFixture));
            var rebuilt = new FlvTestBuilder();
            foreach (var message in media)
            {
                rebuilt.Tag((byte)message.Type, message.Timestamp, message.Payload.ToArray());
            }

            var received = FlvDemuxerTest.Demux(rebuilt.ToArray());
            received.Packets.Should().HaveCount(original.Packets.Count);
            for (var i = 0; i < original.Packets.Count; i++)
            {
                received.Packets[i].TrackId.Should().Be(original.Packets[i].TrackId);
                received.Packets[i].Pts.Should().Be(original.Packets[i].Pts);
                received.Packets[i].Dts.Should().Be(original.Packets[i].Dts);
                received.Packets[i].IsKeyframe.Should().Be(original.Packets[i].IsKeyframe);
                received.Packets[i].Payload.ToArray().Should().Equal(original.Packets[i].Payload.ToArray());
            }
        }

        private static string TcUrlPort(RtmpCommand connect)
        {
            var tcUrl = connect.CommandObject.GetProperty("tcUrl")!.String!;
            return tcUrl["rtmp://127.0.0.1:".Length..tcUrl.LastIndexOf('/')];
        }
    }
}
