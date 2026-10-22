using EggEncoder.Containers.Flv;
using EggEncoder.Media;
using FluentAssertions;

namespace EggEncoder.UnitTests.Containers.Flv
{
    public class FlvTagBodyParserTest
    {
        [Fact]
        public void ParseVideo_AvcSequenceHeader_Should_Return_The_AvcC()
        {
            var body = FlvTagBodyParser.ParseVideo([0x17, 0, 0, 0, 0, 1, 2, 3]);

            body.PacketType.Should().Be(FlvPacketType.SequenceHeader);
            body.Kind.Should().Be(MediaKind.Video);
            body.Codec.Should().Be(MediaCodec.H264);
            body.Payload.ToArray().Should().Equal(1, 2, 3);
            body.UnsupportedReason.Should().BeNull();
        }

        [Theory]
        [InlineData(0x00, 0x00, 0x50, 80)]
        [InlineData(0xFF, 0xFF, 0xD8, -40)]
        [InlineData(0x7F, 0xFF, 0xFF, 8388607)]
        [InlineData(0x80, 0x00, 0x00, -8388608)]
        public void ParseVideo_AvcNalu_Should_Read_Signed_CompositionTime(byte b0, byte b1, byte b2, int expected)
        {
            var body = FlvTagBodyParser.ParseVideo([0x27, 1, b0, b1, b2, 9]);

            body.PacketType.Should().Be(FlvPacketType.CodedFrame);
            body.IsKeyframe.Should().BeFalse();
            body.CompositionTime.Should().Be(expected);
            body.Payload.ToArray().Should().Equal(9);
        }

        [Theory]
        [InlineData(0x17, true)]
        [InlineData(0x47, true)] // generated keyframe
        [InlineData(0x27, false)]
        [InlineData(0x37, false)] // disposable inter frame
        public void ParseVideo_FrameType_Should_Set_Keyframe(byte first, bool keyframe)
        {
            FlvTagBodyParser.ParseVideo([first, 1, 0, 0, 0]).IsKeyframe.Should().Be(keyframe);
        }

        [Fact]
        public void ParseVideo_AvcEndOfSequence_Should_Be_SequenceEnd()
        {
            FlvTagBodyParser.ParseVideo([0x17, 2, 0, 0, 0]).PacketType.Should().Be(FlvPacketType.SequenceEnd);
        }

        [Fact]
        public void ParseVideo_UnknownAvcPacketType_Should_Be_Unsupported()
        {
            var body = FlvTagBodyParser.ParseVideo([0x17, 3, 0, 0, 0]);

            body.PacketType.Should().Be(FlvPacketType.Unsupported);
            body.UnsupportedReason.Should().Contain("AVC packet type 3");
        }

        [Fact]
        public void ParseVideo_LegacyHevcCodecId12_Should_Be_H265()
        {
            var body = FlvTagBodyParser.ParseVideo([0x1C, 1, 0, 0, 0, 7]);

            body.Codec.Should().Be(MediaCodec.H265);
            body.PacketType.Should().Be(FlvPacketType.CodedFrame);
        }

        [Fact]
        public void ParseVideo_CommandFrame_Should_Be_Ignored()
        {
            FlvTagBodyParser.ParseVideo([0x57, 0]).PacketType.Should().Be(FlvPacketType.Ignored);
        }

        [Theory]
        [InlineData(0x12)] // Sorenson H.263
        [InlineData(0x14)] // VP6
        public void ParseVideo_UnsupportedLegacyCodec_Should_Be_Unsupported(byte first)
        {
            var body = FlvTagBodyParser.ParseVideo([first, 0, 0]);

            body.PacketType.Should().Be(FlvPacketType.Unsupported);
            body.Codec.Should().BeNull();
            body.UnsupportedReason.Should().Contain("CodecID");
        }

        [Theory]
        [InlineData(new byte[0])]
        [InlineData(new byte[] { 0x17, 1, 0, 0 })]
        [InlineData(new byte[] { 0x90, (byte)'h', (byte)'v' })]
        [InlineData(new byte[] { 0x91, (byte)'h', (byte)'v', (byte)'c', (byte)'1', 0, 0 })]
        public void ParseVideo_TooShortForHeader_Should_Throw(byte[] body)
        {
            Action act = () => FlvTagBodyParser.ParseVideo(body);

            act.Should().Throw<InvalidDataException>().WithMessage("*too short*");
        }

        [Theory]
        [InlineData("hvc1", MediaCodec.H265)]
        [InlineData("avc1", MediaCodec.H264)]
        [InlineData("av01", MediaCodec.Av1)]
        [InlineData("vp09", MediaCodec.Vp9)]
        public void ParseVideo_ExSequenceStart_Should_Map_FourCc(string fourCc, MediaCodec codec)
        {
            var body = FlvTagBodyParser.ParseVideo([0x90, .. System.Text.Encoding.ASCII.GetBytes(fourCc), 0xAA]);

            body.PacketType.Should().Be(FlvPacketType.SequenceHeader);
            body.Codec.Should().Be(codec);
            body.IsKeyframe.Should().BeTrue();
            body.Payload.ToArray().Should().Equal(0xAA);
        }

        [Fact]
        public void ParseVideo_ExCodedFramesHevc_Should_Read_CompositionTime()
        {
            var body = FlvTagBodyParser.ParseVideo([0xA1, (byte)'h', (byte)'v', (byte)'c', (byte)'1', 0, 0, 0x28, 5, 6]);

            body.PacketType.Should().Be(FlvPacketType.CodedFrame);
            body.IsKeyframe.Should().BeFalse();
            body.CompositionTime.Should().Be(40);
            body.Payload.ToArray().Should().Equal(5, 6);
        }

        [Fact]
        public void ParseVideo_ExCodedFramesX_Should_Have_Zero_CompositionTime()
        {
            var body = FlvTagBodyParser.ParseVideo([0x93, (byte)'h', (byte)'v', (byte)'c', (byte)'1', 5, 6]);

            body.CompositionTime.Should().Be(0);
            body.IsKeyframe.Should().BeTrue();
            body.Payload.ToArray().Should().Equal(5, 6);
        }

        [Fact]
        public void ParseVideo_ExCodedFramesAv1_Should_Have_No_CompositionTime_Field()
        {
            var body = FlvTagBodyParser.ParseVideo([0x91, (byte)'a', (byte)'v', (byte)'0', (byte)'1', 5, 6]);

            body.Codec.Should().Be(MediaCodec.Av1);
            body.CompositionTime.Should().Be(0);
            body.Payload.ToArray().Should().Equal(5, 6);
        }

        [Fact]
        public void ParseVideo_ExSequenceEnd_Should_Be_SequenceEnd()
        {
            FlvTagBodyParser.ParseVideo([0x92, (byte)'h', (byte)'v', (byte)'c', (byte)'1']).PacketType.Should().Be(FlvPacketType.SequenceEnd);
        }

        [Theory]
        [InlineData(0xD4)] // command frame, Metadata packet (ffmpeg's colorInfo)
        [InlineData(0x94)] // keyframe, Metadata packet
        [InlineData(0xD1)] // command frame
        public void ParseVideo_ExMetadataOrCommand_Should_Be_Ignored(byte first)
        {
            FlvTagBodyParser.ParseVideo([first, (byte)'h', (byte)'v', (byte)'c', (byte)'1', 2]).PacketType.Should().Be(FlvPacketType.Ignored);
        }

        [Theory]
        [InlineData(new byte[] { 0xD0, 0x00 })] // command frame, VideoCommand byte with no FourCC
        [InlineData(new byte[] { 0xD1 })]
        public void ParseVideo_ExCommandFrameWithoutFourCc_Should_Be_Ignored(byte[] body)
        {
            FlvTagBodyParser.ParseVideo(body).PacketType.Should().Be(FlvPacketType.Ignored);
        }

        [Fact]
        public void ParseVideo_ExMetadataTooShortForFourCc_Should_Throw()
        {
            Action act = () => FlvTagBodyParser.ParseVideo([0xD4, (byte)'h']);

            act.Should().Throw<InvalidDataException>();
        }

        [Theory]
        [InlineData(0x95)] // MPEG2TSSequenceStart
        [InlineData(0x96)] // Multitrack
        [InlineData(0x97)] // ModEx
        public void ParseVideo_ExUnsupportedPacketType_Should_Be_Unsupported(byte first)
        {
            var body = FlvTagBodyParser.ParseVideo([first]);

            body.PacketType.Should().Be(FlvPacketType.Unsupported);
            body.UnsupportedReason.Should().Contain("packet type");
        }

        [Fact]
        public void ParseVideo_ExUnknownFourCc_Should_Be_Unsupported()
        {
            var body = FlvTagBodyParser.ParseVideo([0x91, (byte)'x', (byte)'y', (byte)'z', (byte)'w', 0, 0, 0]);

            body.PacketType.Should().Be(FlvPacketType.Unsupported);
            body.UnsupportedReason.Should().Contain("FourCC");
        }

        [Fact]
        public void ParseAudio_AacSequenceHeaderAndRaw_Should_Be_Classified()
        {
            var config = FlvTagBodyParser.ParseAudio([0xAF, 0, 0x12, 0x10]);
            var raw = FlvTagBodyParser.ParseAudio([0xAF, 1, 0x21]);

            config.PacketType.Should().Be(FlvPacketType.SequenceHeader);
            config.Codec.Should().Be(MediaCodec.Aac);
            config.Kind.Should().Be(MediaKind.Audio);
            config.SoundRate.Should().Be(44100);
            config.SoundChannels.Should().Be(2);
            config.Payload.ToArray().Should().Equal(0x12, 0x10);
            raw.PacketType.Should().Be(FlvPacketType.CodedFrame);
            raw.IsKeyframe.Should().BeTrue();
            raw.Payload.ToArray().Should().Equal(0x21);
        }

        [Fact]
        public void ParseAudio_UnknownAacPacketType_Should_Be_Unsupported()
        {
            FlvTagBodyParser.ParseAudio([0xAF, 2]).PacketType.Should().Be(FlvPacketType.Unsupported);
        }

        [Theory]
        [InlineData(0x22, 5512, 1)]
        [InlineData(0x26, 11025, 1)]
        [InlineData(0x2B, 22050, 2)]
        [InlineData(0x2E, 44100, 1)]
        [InlineData(0xE2, 8000, 1)]
        public void ParseAudio_Mp3_Should_Read_Rate_And_Channel_Hints(byte first, int rate, int channels)
        {
            var body = FlvTagBodyParser.ParseAudio([first, 0xFF, 0xFB]);

            body.Codec.Should().Be(MediaCodec.Mp3);
            body.PacketType.Should().Be(FlvPacketType.CodedFrame);
            body.SoundRate.Should().Be(rate);
            body.SoundChannels.Should().Be(channels);
            body.Payload.ToArray().Should().Equal(0xFF, 0xFB);
        }

        [Theory]
        [InlineData(0x00)] // linear PCM
        [InlineData(0x60)] // Nellymoser
        [InlineData(0x90)] // ExHeader (Enhanced RTMP v2 audio)
        [InlineData(0xB0)] // Speex
        public void ParseAudio_UnsupportedSoundFormat_Should_Be_Unsupported(byte first)
        {
            var body = FlvTagBodyParser.ParseAudio([first, 0]);

            body.PacketType.Should().Be(FlvPacketType.Unsupported);
            body.UnsupportedReason.Should().Contain("SoundFormat");
        }

        [Theory]
        [InlineData(new byte[0])]
        [InlineData(new byte[] { 0xAF })]
        public void ParseAudio_TooShort_Should_Throw(byte[] body)
        {
            Action act = () => FlvTagBodyParser.ParseAudio(body);

            act.Should().Throw<InvalidDataException>();
        }
    }
}
