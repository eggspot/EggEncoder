using EggEncoder.Streaming.Hls;
using FluentAssertions;

namespace EggEncoder.UnitTests.Streaming.Hls
{
    public class M3u8PlaylistWriterTest
    {
        [Fact]
        public void BuildMediaPlaylist_VodWithSegments_Should_ProduceExpectedGrammar()
        {
            var segments = new[]
            {
                new HlsSegmentDescriptor(1, 6.0, "segment_1.m4s"),
                new HlsSegmentDescriptor(2, 6.0, "segment_2.m4s"),
                new HlsSegmentDescriptor(3, 2.5, "segment_3.m4s")
            };

            var playlist = M3u8PlaylistWriter.BuildMediaPlaylist(6, 1, segments, "init.mp4", endList: true);
            var lines = playlist.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            lines[0].Should().Be("#EXTM3U");
            lines.Should().ContainSingle(l => l == "#EXT-X-VERSION:6");
            lines.Should().ContainSingle(l => l == "#EXT-X-TARGETDURATION:6");
            lines.Should().ContainSingle(l => l == "#EXT-X-MEDIA-SEQUENCE:1");
            lines.Should().ContainSingle(l => l == "#EXT-X-MAP:URI=\"init.mp4\"");
            lines[^1].Should().Be("#EXT-X-ENDLIST");

            var extinfLines = lines.Where(l => l.StartsWith("#EXTINF:")).ToList();
            var uriLines = lines.Where(l => l is "segment_1.m4s" or "segment_2.m4s" or "segment_3.m4s").ToList();
            extinfLines.Should().HaveCount(3);
            uriLines.Should().HaveCount(3, "every #EXTINF line must be paired 1:1 with exactly one following URI line");

            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("#EXTINF:"))
                {
                    uriLines.Should().Contain(lines[i + 1], "the line immediately after #EXTINF must be that segment's own URI");
                }
            }
        }

        [Fact]
        public void BuildMediaPlaylist_LiveInProgress_Should_OmitEndList()
        {
            var playlist = M3u8PlaylistWriter.BuildMediaPlaylist(6, 1, [new HlsSegmentDescriptor(1, 6.0, "segment_1.m4s")], "init.mp4", endList: false);

            playlist.Should().NotContain("#EXT-X-ENDLIST");
        }

        [Fact]
        public void BuildMediaPlaylist_WithNoInitSegmentUri_Should_OmitExtXMap()
        {
            var playlist = M3u8PlaylistWriter.BuildMediaPlaylist(6, 1, [], null, endList: true);

            playlist.Should().NotContain("#EXT-X-MAP");
        }

        [Fact]
        public void BuildMediaPlaylist_WithNoSegments_Should_StillProduceValidHeader()
        {
            var playlist = M3u8PlaylistWriter.BuildMediaPlaylist(6, 1, [], "init.mp4", endList: false);

            playlist.Should().Contain("#EXTM3U");
            playlist.Should().Contain("#EXT-X-TARGETDURATION:6");
            playlist.Should().NotContain("#EXTINF");
        }

        [Fact]
        public void BuildMediaPlaylist_WithNullSegments_Should_Throw()
        {
            var act = () => M3u8PlaylistWriter.BuildMediaPlaylist(6, 1, null!, "init.mp4", true);

            act.Should().ThrowExactly<ArgumentNullException>();
        }
    }
}
