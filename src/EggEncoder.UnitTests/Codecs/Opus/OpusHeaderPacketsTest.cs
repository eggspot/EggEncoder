using EggEncoder.Codecs.Opus;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Opus
{
    public class OpusHeaderPacketsTest
    {
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void BuildOpusHead_Then_ParseOpusHead_Should_RoundTrip(int channels)
        {
            var packet = OpusHeaderPackets.BuildOpusHead(channels, preSkip: 312, inputSampleRate: 48000);

            var head = OpusHeaderPackets.ParseOpusHead(packet, "test.opus");

            head.Channels.Should().Be(channels);
            head.PreSkip.Should().Be(312);
            head.InputSampleRate.Should().Be(48000);
        }

        [Fact]
        public void BuildOpusHead_Should_Produce_NineteenBytePacket_ForFamilyZero()
        {
            var packet = OpusHeaderPackets.BuildOpusHead(2, preSkip: 0, inputSampleRate: 48000);

            packet.Should().HaveCount(19);
        }

        [Fact]
        public void ParseOpusHead_WithMissingMagic_Should_Throw()
        {
            var packet = OpusHeaderPackets.BuildOpusHead(1, preSkip: 0, inputSampleRate: 48000);
            packet[0] = (byte)'X';

            var act = () => OpusHeaderPackets.ParseOpusHead(packet, "test.opus");

            act.Should().ThrowExactly<InvalidDataException>().WithMessage("*OpusHead*");
        }

        [Fact]
        public void ParseOpusHead_WithTooShortPacket_Should_Throw()
        {
            var act = () => OpusHeaderPackets.ParseOpusHead(new byte[5], "test.opus");

            act.Should().ThrowExactly<InvalidDataException>();
        }

        [Fact]
        public void ParseOpusHead_WithUnsupportedVersion_Should_Throw()
        {
            var packet = OpusHeaderPackets.BuildOpusHead(1, preSkip: 0, inputSampleRate: 48000);
            packet[8] = 2;

            var act = () => OpusHeaderPackets.ParseOpusHead(packet, "test.opus");

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(3)]
        public void ParseOpusHead_WithUnsupportedChannelCount_Should_Throw(int channels)
        {
            var packet = OpusHeaderPackets.BuildOpusHead(1, preSkip: 0, inputSampleRate: 48000);
            packet[9] = (byte)channels;

            var act = () => OpusHeaderPackets.ParseOpusHead(packet, "test.opus");

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void ParseOpusHead_WithNonZeroChannelMappingFamily_Should_Throw()
        {
            var packet = OpusHeaderPackets.BuildOpusHead(2, preSkip: 0, inputSampleRate: 48000);
            packet[18] = 1;

            var act = () => OpusHeaderPackets.ParseOpusHead(packet, "test.opus");

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void BuildOpusTags_Then_ParseOpusTags_Should_Not_Throw()
        {
            var packet = OpusHeaderPackets.BuildOpusTags();

            var act = () => OpusHeaderPackets.ParseOpusTags(packet, "test.opus");

            act.Should().NotThrow();
        }

        [Fact]
        public void ParseOpusTags_WithMissingMagic_Should_Throw()
        {
            var packet = OpusHeaderPackets.BuildOpusTags();
            packet[0] = (byte)'X';

            var act = () => OpusHeaderPackets.ParseOpusTags(packet, "test.opus");

            act.Should().ThrowExactly<InvalidDataException>().WithMessage("*OpusTags*");
        }

        [Fact]
        public void ParseOpusTags_WithTooShortPacket_Should_Throw()
        {
            var act = () => OpusHeaderPackets.ParseOpusTags(new byte[3], "test.opus");

            act.Should().ThrowExactly<InvalidDataException>();
        }

        [Fact]
        public void BuildOpusTags_Then_ParseOpusTags_Should_RoundTrip_Tags()
        {
            var tags = new Dictionary<string, string> { ["TITLE"] = "Title", ["ARTIST"] = "Artist" };
            var packet = OpusHeaderPackets.BuildOpusTags(tags);

            var parsedTags = OpusHeaderPackets.ParseOpusTags(packet, "test.opus");

            parsedTags.Should().ContainKey("TITLE").WhoseValue.Should().Be("Title");
            parsedTags.Should().ContainKey("ARTIST").WhoseValue.Should().Be("Artist");
        }

        [Fact]
        public void BuildOpusTags_WithNullTags_Should_ProduceEmptyParsedResult()
        {
            var packet = OpusHeaderPackets.BuildOpusTags(tags: null);

            var parsedTags = OpusHeaderPackets.ParseOpusTags(packet, "test.opus");

            parsedTags.Should().BeEmpty();
        }

        [Fact]
        public void BuildOpusTags_WithInvalidFieldName_Should_Throw()
        {
            var tags = new Dictionary<string, string> { ["BAD=NAME"] = "value" };

            var act = () => OpusHeaderPackets.BuildOpusTags(tags);

            act.Should().Throw<ArgumentException>();
        }
    }
}
