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

        [Fact]
        public void Probe_WithSyntheticStreamHavingNoId3v2Tag_Should_Return_NullTags()
        {
            // None of this directory's own real fixtures lack an ID3v2 tag (every one of them
            // carries ffmpeg's own real TSSE encoder-identification frame -- see the next test),
            // so the "genuinely no tag at all" case is exercised directly against Id3v2Reader
            // (see Id3v2TagTest's own ReadTags_WithNoId3v2Header_Should_ReturnNull) rather than
            // against a real fixture here.
            Id3v2Reader.ReadTags(new MemoryStream(new byte[32])).Should().BeNull();
        }

        [Fact]
        public void Probe_RealFixtureFile_Should_Decode_Its_Own_RealId3v2Tag()
        {
            // cbr128.mp3 (like every fixture in this directory) carries a real, ffmpeg-produced
            // ID3v2.4 tag with a single TSSE (encoder) text frame -- confirmed by inspecting the
            // file's own raw bytes, not synthesized for this test.
            var result = Mp3Probe.Probe(Path.GetFullPath("Codecs/Mp3/cbr128.mp3"));

            result.SampleRate.Should().Be(44100);
            result.Channels.Should().Be(2);
            result.DurationInSeconds.Should().Be(10);
            result.Tags.Should().NotBeNull();
            result.Tags!.Should().ContainKey("TSSE").WhoseValue.Should().Be("Lavf59.10.100");
        }
    }
}
