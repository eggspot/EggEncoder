using EggEncoder.Codecs.Mp3;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Mp3
{
    public class Mp3ProbeTest
    {
        [Fact]
        public void Probe_CbrFileWithInfoHeader_Should_Report_Cbr_And_Match_FfprobeGroundTruth()
        {
            var result = Mp3Probe.Probe(Path.GetFullPath("Codecs/Mp3/cbr128.mp3"));

            result.DurationInSeconds.Should().Be(10);
            result.SampleRate.Should().Be(44100);
            result.Channels.Should().Be(2);
            result.BitRate.Should().Be(128000);
            result.IsVariableBitRate.Should().BeFalse();
        }

        [Fact]
        public void Probe_VbrFileWithXingHeader_Should_Report_Vbr_And_Match_FfprobeGroundTruth()
        {
            var result = Mp3Probe.Probe(Path.GetFullPath("Codecs/Mp3/vbr_q4.mp3"));

            result.DurationInSeconds.Should().Be(10);
            result.SampleRate.Should().Be(44100);
            result.Channels.Should().Be(2);
            result.BitRate.Should().Be(32000);
            result.IsVariableBitRate.Should().BeTrue();
        }

        [Fact]
        public void Probe_CbrFileWithNoVbrHeader_Should_FallBackTo_FileSizeEstimate()
        {
            var result = Mp3Probe.Probe(Path.GetFullPath("Codecs/Mp3/no_xing.mp3"));

            result.DurationInSeconds.Should().Be(10);
            result.SampleRate.Should().Be(44100);
            result.Channels.Should().Be(2);
            result.BitRate.Should().Be(128000);
            result.IsVariableBitRate.Should().BeFalse();
        }

        [Fact]
        public void Probe_WithMissingFile_Should_Throw()
        {
            var act = () => Mp3Probe.Probe(Path.GetFullPath("Codecs/Mp3/does_not_exist.mp3"));
            act.Should().ThrowExactly<FileNotFoundException>();
        }
    }
}
