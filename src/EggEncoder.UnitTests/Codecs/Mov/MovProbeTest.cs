using EggEncoder.Codecs.Mov;
using EggEncoder.UnitTests.TestUtilities;
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

        [Fact]
        public void Probe_Stream_Should_Match_PathBased_Result()
        {
            using var stream = File.OpenRead(Path.GetFullPath("Codecs/Mov/test.mov"));

            var result = MovProbe.Probe(stream);

            result.DurationInSeconds.Should().Be(5);
            result.Width.Should().Be(640);
            result.Height.Should().Be(360);
            result.CodecFourCc.Should().Be("avc1");
        }

        [Fact]
        public void Probe_Stream_Should_Not_Dispose_TheCallersStream()
        {
            using var stream = File.OpenRead(Path.GetFullPath("Codecs/Mov/test.mov"));

            MovProbe.Probe(stream);

            stream.CanRead.Should().BeTrue();
        }

        [Fact]
        public void Probe_WithNullStream_Should_Throw()
        {
            var act = () => MovProbe.Probe((Stream)null!);
            act.Should().ThrowExactly<ArgumentNullException>();
        }

        [Fact]
        public void Probe_WithNonSeekableStream_Should_Throw()
        {
            var bytes = File.ReadAllBytes(Path.GetFullPath("Codecs/Mov/test.mov"));
            using var stream = new NonSeekableStream(bytes);

            var act = () => MovProbe.Probe(stream);

            act.Should().ThrowExactly<ArgumentException>().WithMessage("*seekable*");
        }
    }
}
