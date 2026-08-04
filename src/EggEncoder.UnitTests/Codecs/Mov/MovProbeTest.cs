using EggEncoder.Codecs.Mov;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Mov
{
    public class MovProbeTest
    {
        [Fact]
        public void Probe_MovFile_Should_Match_FfprobeGroundTruth()
        {
            var result = MovProbe.Probe(Path.GetFullPath("Codecs/Mov/test.mov"));

            result.DurationInSeconds.Should().Be(5);
            result.Width.Should().Be(640);
            result.Height.Should().Be(360);
            result.CodecFourCc.Should().Be("avc1");
        }

        [Fact]
        public void Probe_Mp4File_Should_Match_FfprobeGroundTruth()
        {
            var result = MovProbe.Probe(Path.GetFullPath("Codecs/Mov/test.mp4"));

            result.DurationInSeconds.Should().Be(5);
            result.Width.Should().Be(640);
            result.Height.Should().Be(360);
            result.CodecFourCc.Should().Be("avc1");
        }

        [Fact]
        public void Probe_WithMissingFile_Should_Throw()
        {
            var act = () => MovProbe.Probe(Path.GetFullPath("Codecs/Mov/does_not_exist.mov"));
            act.Should().ThrowExactly<FileNotFoundException>();
        }
    }
}
