using EggEncoder.Media;
using EggEncoder.UnitTests.Containers.Mp4;
using FluentAssertions;

namespace EggEncoder.UnitTests.Media
{
    public class MediaFormatSnifferTest
    {
        [Theory]
        [InlineData("00000018667479706D703432", ".mp4")]
        [InlineData("52494646000000005741564566", ".wav")]
        [InlineData("464F524D0000000041494646", ".aiff")]
        [InlineData("464F524D0000000041494643", ".aiff")]
        [InlineData("664C614300000022", ".flac")]
        [InlineData("4F67675300020000000000000000", ".ogg")]
        [InlineData("2E736E6400000018", ".au")]
        [InlineData("6361666600010000", ".caf")]
        [InlineData("5454413101000200", ".tta")]
        [InlineData("7776706B00000000", ".wv")]
        [InlineData("3026B2758E66CF11", ".wma")]
        [InlineData("464C5601050000", ".flv")]
        [InlineData("494433040000", ".mp3")]
        [InlineData("FFFB9064", ".mp3")]
        [InlineData("FFF15080", ".aac")]
        [InlineData("FFF95080", ".aac")]
        [InlineData("FFD8FFE0", ".jpg")]
        [InlineData("0000000000", null)]
        [InlineData("FF", null)]
        [InlineData("", null)]
        [InlineData("52494646000000004156492000", null)]
        public void DetectExtension_Should_Recognise_The_Magic_Bytes(string hex, string? expected)
        {
            MediaFormatSniffer.DetectExtension(Convert.FromHexString(hex)).Should().Be(expected);
        }

        [Fact]
        public void DetectExtension_Ogg_With_OpusHead_Should_Be_Opus()
        {
            var header = new byte[64];
            "OggS"u8.CopyTo(header);
            "OpusHead"u8.CopyTo(header.AsSpan(28));

            MediaFormatSniffer.DetectExtension(header).Should().Be(".opus");
        }

        [Fact]
        public void ResolveExtension_Should_Prefer_The_Name_And_Sniff_When_It_Has_None()
        {
            var directory = Directory.CreateTempSubdirectory("eggencoder-sniff-").FullName;
            try
            {
                var bare = Path.Combine(directory, "clip");
                File.Copy(Mp4FileSourceTest.H264AacFixture, bare);
                var named = Path.Combine(directory, "clip.WAV");
                File.Copy(Mp4FileSourceTest.H264AacFixture, named);
                var tiny = Path.Combine(directory, "tiny");
                File.WriteAllBytes(tiny, [1]);

                MediaFormatSniffer.ResolveExtension(bare).Should().Be(".mp4");
                MediaFormatSniffer.ResolveExtension(named).Should().Be(".wav");
                MediaFormatSniffer.ResolveExtension(tiny).Should().BeNull();
                MediaFormatSniffer.DetectExtension(bare).Should().Be(".mp4");
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void Path_Overloads_Null_Should_Throw()
        {
            ((Action)(() => MediaFormatSniffer.DetectExtension((string)null!))).Should().Throw<ArgumentNullException>();
            ((Action)(() => MediaFormatSniffer.ResolveExtension(null!))).Should().Throw<ArgumentNullException>();
        }
    }
}
