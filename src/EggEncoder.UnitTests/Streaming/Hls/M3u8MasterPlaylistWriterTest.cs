using EggEncoder.Streaming.Hls;
using FluentAssertions;

namespace EggEncoder.UnitTests.Streaming.Hls
{
    public class M3u8MasterPlaylistWriterTest
    {
        [Fact]
        public void BuildMasterPlaylist_WithMultipleRenditions_Should_ProduceExpectedGrammar()
        {
            var renditions = new[]
            {
                new HlsRenditionDescriptor("low/playlist.m3u8", 64000, "mp4a.40.2"),
                new HlsRenditionDescriptor("high/playlist.m3u8", 128000, "mp4a.40.2")
            };

            var playlist = M3u8MasterPlaylistWriter.BuildMasterPlaylist(renditions);
            var lines = playlist.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            lines[0].Should().Be("#EXTM3U");
            lines.Should().ContainSingle(l => l == $"#EXT-X-VERSION:{M3u8PlaylistWriter.Version}");

            var streamInfLines = lines.Where(l => l.StartsWith("#EXT-X-STREAM-INF:")).ToList();
            streamInfLines.Should().HaveCount(2);
            streamInfLines[0].Should().Be("#EXT-X-STREAM-INF:BANDWIDTH=64000,CODECS=\"mp4a.40.2\"");
            streamInfLines[1].Should().Be("#EXT-X-STREAM-INF:BANDWIDTH=128000,CODECS=\"mp4a.40.2\"");

            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("#EXT-X-STREAM-INF:"))
                {
                    lines[i + 1].Should().Be(lines[i].Contains("64000") ? "low/playlist.m3u8" : "high/playlist.m3u8", "the line immediately after #EXT-X-STREAM-INF must be that rendition's own playlist URI");
                }
            }
        }

        [Fact]
        public void BuildMasterPlaylist_WithNoRenditions_Should_StillProduceValidHeader()
        {
            var playlist = M3u8MasterPlaylistWriter.BuildMasterPlaylist([]);

            playlist.Should().Contain("#EXTM3U");
            playlist.Should().NotContain("#EXT-X-STREAM-INF");
        }

        [Fact]
        public void BuildMasterPlaylist_WithNullRenditions_Should_Throw()
        {
            var act = () => M3u8MasterPlaylistWriter.BuildMasterPlaylist(null!);

            act.Should().ThrowExactly<ArgumentNullException>();
        }
    }
}
