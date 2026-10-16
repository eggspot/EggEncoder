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
        public void Probe_CbrFileWithInfoHeader_Should_Report_EncoderDelayAndPadding_Matching_RealLameTag()
        {
            // cbr128.mp3 carries a real, ffmpeg (Lavc)-produced LAME-style tag extension --
            // confirmed directly from this fixture's own raw bytes (frames=384, start_pad=576,
            // end_pad=792; 384*1152 - 576 - 792 = 441000 samples = exactly 10.0s at 44100Hz), not
            // assumed. 576 is LAME's/Lavc's own well-known default encoder delay.
            var result = Mp3Probe.Probe(Path.GetFullPath("Codecs/Mp3/cbr128.mp3"));

            result.EncoderDelaySamples.Should().Be(576);
            result.EncoderPaddingSamples.Should().Be(792);
        }

        [Fact]
        public void Probe_FileWithNoXingHeaderAtAll_Should_Report_NullEncoderDelayAndPadding()
        {
            // no_xing.mp3 has no Xing/Info header at all, the broadest case that must yield
            // null -- a Xing/Info header present but lacking the LAME-style extension specifically
            // (a narrower, real case on its own) has no dedicated fixture in this directory to
            // exercise distinctly, since every real fixture here happens to carry the full
            // extension; TryReadLameTagDelayAndPadding's own length guard covers that case too.
            var result = Mp3Probe.Probe(Path.GetFullPath("Codecs/Mp3/no_xing.mp3"));

            result.EncoderDelaySamples.Should().BeNull();
            result.EncoderPaddingSamples.Should().BeNull();
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
        public void Probe_WithXingHeaderTruncatedRightAfterItsFlags_Should_NotThrow_And_FallBackTo_FileSizeEstimate()
        {
            // vbr_q4.mp3's own real Xing header declares flags 0x0F (frame-count, byte-count, TOC,
            // and VBR-quality all present) -- truncating the file to end exactly 8 bytes into that
            // header (the "Xing" tag plus its 4-byte flags field, nothing more) means none of the
            // four fields the flags claim to carry actually exist. TryReadVbrHeader must degrade
            // gracefully to "no usable VBR info" the same way it already does for a file with no
            // Xing/Info header at all, not throw EndOfStreamException from an unguarded ReadExactly
            // on a field the flags promised but the file doesn't actually contain.
            var tempPath = Path.Combine(Path.GetTempPath(), $"vbr_truncated_{Guid.NewGuid():N}.mp3");
            try
            {
                var bytes = File.ReadAllBytes(Path.GetFullPath("Codecs/Mp3/vbr_q4.mp3"));
                var tagOffset = IndexOf(bytes, "Xing"u8);
                tagOffset.Should().BeGreaterThan(0, "the fixture is expected to carry a real Xing header");

                File.WriteAllBytes(tempPath, bytes[..(tagOffset + 8)]);

                var act = () => Mp3Probe.Probe(tempPath);

                act.Should().NotThrow();
                var result = act();
                result.IsVariableBitRate.Should().BeFalse();
                result.EncoderDelaySamples.Should().BeNull();
                result.EncoderPaddingSamples.Should().BeNull();
            }
            finally
            {
                File.Delete(tempPath);
            }
        }

        private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle)
        {
            return haystack.AsSpan().IndexOf(needle);
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
