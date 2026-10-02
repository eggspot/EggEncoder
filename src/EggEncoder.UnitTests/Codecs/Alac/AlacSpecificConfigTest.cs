using EggEncoder.Codecs.Alac;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Alac
{
    public class AlacSpecificConfigTest
    {
        [Fact]
        public void WriteTo_Then_Parse_Should_RoundTrip()
        {
            var config = new AlacSpecificConfig
            {
                FrameLength = 4096,
                BitDepth = 16,
                Pb = 40,
                Mb = 10,
                Kb = 14,
                NumChannels = 1,
                MaxRun = 255,
                SampleRate = 44100
            };

            var bytes = new byte[AlacSpecificConfig.EncodedSize];
            config.WriteTo(bytes);

            var parsed = AlacSpecificConfig.Parse(bytes);

            parsed.FrameLength.Should().Be(config.FrameLength);
            parsed.BitDepth.Should().Be(config.BitDepth);
            parsed.Pb.Should().Be(config.Pb);
            parsed.Mb.Should().Be(config.Mb);
            parsed.Kb.Should().Be(config.Kb);
            parsed.NumChannels.Should().Be(config.NumChannels);
            parsed.MaxRun.Should().Be(config.MaxRun);
            parsed.SampleRate.Should().Be(config.SampleRate);
        }

        [Fact]
        public void Parse_TooFewBytes_Should_Throw()
        {
            var act = () => AlacSpecificConfig.Parse(new byte[AlacSpecificConfig.EncodedSize - 1]);

            act.Should().ThrowExactly<InvalidDataException>();
        }

        [Fact]
        public void Parse_NonPositiveFrameLength_Should_Throw()
        {
            var bytes = new byte[AlacSpecificConfig.EncodedSize];
            new AlacSpecificConfig { FrameLength = 1, BitDepth = 16, Pb = 40, Mb = 10, Kb = 14, NumChannels = 1, MaxRun = 255, SampleRate = 44100 }.WriteTo(bytes);
            bytes[0] = 0;
            bytes[1] = 0;
            bytes[2] = 0;
            bytes[3] = 0; // frameLength = 0

            var act = () => AlacSpecificConfig.Parse(bytes);

            act.Should().ThrowExactly<InvalidDataException>();
        }

        [Fact]
        public void Parse_ZeroChannels_Should_Throw()
        {
            var bytes = new byte[AlacSpecificConfig.EncodedSize];
            new AlacSpecificConfig { FrameLength = 4096, BitDepth = 16, Pb = 40, Mb = 10, Kb = 14, NumChannels = 1, MaxRun = 255, SampleRate = 44100 }.WriteTo(bytes);
            bytes[9] = 0; // numChannels = 0

            var act = () => AlacSpecificConfig.Parse(bytes);

            act.Should().ThrowExactly<InvalidDataException>();
        }

        [Fact]
        public void Parse_ZeroKb_Should_Throw()
        {
            var bytes = new byte[AlacSpecificConfig.EncodedSize];
            new AlacSpecificConfig { FrameLength = 4096, BitDepth = 16, Pb = 40, Mb = 10, Kb = 14, NumChannels = 1, MaxRun = 255, SampleRate = 44100 }.WriteTo(bytes);
            bytes[8] = 0; // kb = 0

            var act = () => AlacSpecificConfig.Parse(bytes);

            act.Should().ThrowExactly<InvalidDataException>();
        }

        [Fact]
        public void WriteTo_DestinationTooSmall_Should_Throw()
        {
            var config = new AlacSpecificConfig { FrameLength = 4096, BitDepth = 16, Pb = 40, Mb = 10, Kb = 14, NumChannels = 1, MaxRun = 255, SampleRate = 44100 };

            var act = () => config.WriteTo(new byte[AlacSpecificConfig.EncodedSize - 1]);

            act.Should().ThrowExactly<ArgumentException>();
        }
    }
}
