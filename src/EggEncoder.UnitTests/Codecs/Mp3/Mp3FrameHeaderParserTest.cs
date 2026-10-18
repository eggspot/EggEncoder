using EggEncoder.Codecs.Mp3;
using EggEncoder.Transform;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Mp3
{
    public class Mp3FrameHeaderParserTest
    {
        [Fact]
        public void Parse_StereoHeader_Should_RoundTrip_TheEncoderOwnWriteHeader()
        {
            var encoder = new Mp3FrameEncoder(channels: 2, sampleRate: 44100, bitRateKbps: 128);
            var writer = new BitWriter();
            encoder.WriteHeader(writer, padding: 0);

            var reader = new BitReader(writer.ToArray());
            var header = Mp3FrameHeaderParser.Parse(reader);

            header.SampleRate.Should().Be(44100);
            header.BitRateKbps.Should().Be(128);
            header.Channels.Should().Be(2);
            header.HasCrc.Should().BeFalse();
            header.SideInfoSize.Should().Be(32);
            header.FrameLengthBytes.Should().Be((144 * 128 * 1000 / 44100) + 0);
        }

        [Fact]
        public void Parse_MonoHeader_Should_RoundTrip_TheEncoderOwnWriteHeader()
        {
            var encoder = new Mp3FrameEncoder(channels: 1, sampleRate: 48000, bitRateKbps: 64);
            var writer = new BitWriter();
            encoder.WriteHeader(writer, padding: 0);

            var reader = new BitReader(writer.ToArray());
            var header = Mp3FrameHeaderParser.Parse(reader);

            header.SampleRate.Should().Be(48000);
            header.BitRateKbps.Should().Be(64);
            header.Channels.Should().Be(1);
            header.SideInfoSize.Should().Be(17);
        }

        [Fact]
        public void Parse_HeaderWithPaddingBitSet_Should_AddOneByteToFrameLength()
        {
            var encoder = new Mp3FrameEncoder(channels: 2, sampleRate: 44100, bitRateKbps: 128);
            var writer = new BitWriter();
            encoder.WriteHeader(writer, padding: 1);

            var reader = new BitReader(writer.ToArray());
            var header = Mp3FrameHeaderParser.Parse(reader);

            header.FrameLengthBytes.Should().Be((144 * 128 * 1000 / 44100) + 1);
        }

        [Fact]
        public void Parse_WithWrongSyncWord_Should_Throw()
        {
            var writer = new BitWriter();
            writer.WriteBits(0x7FE, 11); // one bit off from the real 0x7FF sync word
            writer.WriteBits(0, 21);

            var reader = new BitReader(writer.ToArray());
            var act = () => Mp3FrameHeaderParser.Parse(reader);

            act.Should().Throw<InvalidDataException>();
        }

        [Fact]
        public void Parse_WithMpeg2VersionBits_Should_Throw()
        {
            var writer = new BitWriter();
            writer.WriteBits(0x7FF, 11);
            writer.WriteBits(0b10, 2); // MPEG-2, not MPEG-1
            writer.WriteBits(0b01, 2);
            writer.WriteBits(0, 17);

            var reader = new BitReader(writer.ToArray());
            var act = () => Mp3FrameHeaderParser.Parse(reader);

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void Parse_WithLayer1Bits_Should_Throw()
        {
            var writer = new BitWriter();
            writer.WriteBits(0x7FF, 11);
            writer.WriteBits(0b11, 2);
            writer.WriteBits(0b11, 2); // Layer I, not Layer III
            writer.WriteBits(0, 17);

            var reader = new BitReader(writer.ToArray());
            var act = () => Mp3FrameHeaderParser.Parse(reader);

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void Parse_WithJointStereoModeBits_Should_Throw()
        {
            var writer = new BitWriter();
            writer.WriteBits(0x7FF, 11);
            writer.WriteBits(0b11, 2); // MPEG-1
            writer.WriteBits(0b01, 2); // Layer III
            writer.WriteBits(1, 1); // no CRC
            writer.WriteBits(9, 4); // bitrate index 9 = 128kbps
            writer.WriteBits(0, 2); // sample rate index 0 = 44100Hz
            writer.WriteBits(0, 1); // padding
            writer.WriteBits(0, 1); // private
            writer.WriteBits(0b01, 2); // channel mode = joint stereo
            writer.WriteBits(0, 2 + 1 + 1 + 2); // mode_extension/copyright/original/emphasis

            var reader = new BitReader(writer.ToArray());
            var act = () => Mp3FrameHeaderParser.Parse(reader);

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void Parse_WithDualChannelModeBits_Should_Succeed_AsIndependentTwoChannel()
        {
            var writer = new BitWriter();
            writer.WriteBits(0x7FF, 11);
            writer.WriteBits(0b11, 2); // MPEG-1
            writer.WriteBits(0b01, 2); // Layer III
            writer.WriteBits(1, 1); // no CRC
            writer.WriteBits(9, 4); // bitrate index 9 = 128kbps
            writer.WriteBits(0, 2); // sample rate index 0 = 44100Hz
            writer.WriteBits(0, 1); // padding
            writer.WriteBits(0, 1); // private
            writer.WriteBits(0b10, 2); // channel mode = dual channel (two independent channels)
            writer.WriteBits(0, 2 + 1 + 1 + 2);

            var reader = new BitReader(writer.ToArray());
            var header = Mp3FrameHeaderParser.Parse(reader);

            header.Channels.Should().Be(2);
        }

        [Fact]
        public void Parse_WithFreeFormatBitrateIndex_Should_Throw()
        {
            var writer = new BitWriter();
            writer.WriteBits(0x7FF, 11);
            writer.WriteBits(0b11, 2);
            writer.WriteBits(0b01, 2);
            writer.WriteBits(1, 1);
            writer.WriteBits(0, 4); // bitrate index 0 = free format
            writer.WriteBits(0, 2);
            writer.WriteBits(0, 1 + 1);
            writer.WriteBits(0b11, 2); // mono
            writer.WriteBits(0, 2 + 1 + 1 + 2);

            var reader = new BitReader(writer.ToArray());
            var act = () => Mp3FrameHeaderParser.Parse(reader);

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void Parse_WithReservedBitrateIndex_Should_Throw()
        {
            var writer = new BitWriter();
            writer.WriteBits(0x7FF, 11);
            writer.WriteBits(0b11, 2);
            writer.WriteBits(0b01, 2);
            writer.WriteBits(1, 1);
            writer.WriteBits(15, 4); // bitrate index 15 = reserved
            writer.WriteBits(0, 2);
            writer.WriteBits(0, 1 + 1);
            writer.WriteBits(0b11, 2); // mono
            writer.WriteBits(0, 2 + 1 + 1 + 2);

            var reader = new BitReader(writer.ToArray());
            var act = () => Mp3FrameHeaderParser.Parse(reader);

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void Parse_WithReservedSampleRateIndex_Should_Throw()
        {
            var writer = new BitWriter();
            writer.WriteBits(0x7FF, 11);
            writer.WriteBits(0b11, 2);
            writer.WriteBits(0b01, 2);
            writer.WriteBits(1, 1);
            writer.WriteBits(9, 4); // bitrate index 9 = 128kbps
            writer.WriteBits(3, 2); // sample rate index 3 = reserved
            writer.WriteBits(0, 1 + 1);
            writer.WriteBits(0b11, 2); // mono
            writer.WriteBits(0, 2 + 1 + 1 + 2);

            var reader = new BitReader(writer.ToArray());
            var act = () => Mp3FrameHeaderParser.Parse(reader);

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void Parse_WithCrcProtectionBitZero_Should_ReportHasCrcTrue()
        {
            var writer = new BitWriter();
            writer.WriteBits(0x7FF, 11);
            writer.WriteBits(0b11, 2);
            writer.WriteBits(0b01, 2);
            writer.WriteBits(0, 1); // protection_bit = 0 -> CRC present
            writer.WriteBits(9, 4);
            writer.WriteBits(0, 2);
            writer.WriteBits(0, 1 + 1);
            writer.WriteBits(0b11, 2);
            writer.WriteBits(0, 2 + 1 + 1 + 2);

            var reader = new BitReader(writer.ToArray());
            var header = Mp3FrameHeaderParser.Parse(reader);

            header.HasCrc.Should().BeTrue();
        }
    }
}
