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
            lines.Should().ContainSingle(l => l == "#EXT-X-VERSION:7");
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

        [Fact]
        public void BuildMediaPlaylist_ZeroDiscontinuitySequence_Should_MatchTheOverloadWithout()
        {
            var segments = new[] { new HlsSegmentDescriptor(1, 2.0, "segment_1.m4s") };

            var withSequence = M3u8PlaylistWriter.BuildMediaPlaylist(2, 1, 0, segments, "init.mp4", endList: true);

            withSequence.Should().Be(M3u8PlaylistWriter.BuildMediaPlaylist(2, 1, segments, "init.mp4", endList: true));
            withSequence.Should().NotContain("#EXT-X-DISCONTINUITY");
        }

        [Fact]
        public void BuildMediaPlaylist_DiscontinuitiesAndInitChanges_Should_TagTheSegmentsTheyApplyTo()
        {
            var segments = new[]
            {
                new HlsSegmentDescriptor(4, 2.0, "segment_4.m4s") { InitSegmentUri = "init_2.mp4" },
                new HlsSegmentDescriptor(5, 1.5, "segment_5.m4s") { Discontinuity = true, InitSegmentUri = "init_3.mp4" },
                new HlsSegmentDescriptor(6, 2.0, "segment_6.m4s") { InitSegmentUri = "init_3.mp4" },
                new HlsSegmentDescriptor(7, 2.0, "segment_7.m4s") { Discontinuity = true }
            };

            var playlist = M3u8PlaylistWriter.BuildMediaPlaylist(2, 4, 3, segments, "init_2.mp4", endList: false);

            playlist.Should().Be(
                "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:4\n#EXT-X-DISCONTINUITY-SEQUENCE:3\n" +
                "#EXT-X-MAP:URI=\"init_2.mp4\"\n#EXTINF:2.000000,\nsegment_4.m4s\n" +
                "#EXT-X-DISCONTINUITY\n#EXT-X-MAP:URI=\"init_3.mp4\"\n#EXTINF:1.500000,\nsegment_5.m4s\n" +
                "#EXTINF:2.000000,\nsegment_6.m4s\n" +
                "#EXT-X-DISCONTINUITY\n#EXTINF:2.000000,\nsegment_7.m4s\n");
        }

        [Fact]
        public void BuildMediaPlaylist_NoInitUriButASegmentNamingOne_Should_MapBeforeThatSegment()
        {
            var segments = new[]
            {
                new HlsSegmentDescriptor(1, 2.0, "segment_1.ts"),
                new HlsSegmentDescriptor(2, 2.0, "segment_2.m4s") { InitSegmentUri = "init.mp4" }
            };

            var playlist = M3u8PlaylistWriter.BuildMediaPlaylist(2, 1, segments, initSegmentUri: null, endList: true);

            playlist.Should().Contain("segment_1.ts\n#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:2.000000,\nsegment_2.m4s\n");
            playlist.Split('\n').Count(l => l.StartsWith("#EXT-X-MAP")).Should().Be(1);
        }

        [Fact]
        public void BuildMediaPlaylist_NegativeDiscontinuitySequence_Should_Throw()
        {
            var act = () => M3u8PlaylistWriter.BuildMediaPlaylist(2, 1, -1, [], null, endList: false);

            act.Should().ThrowExactly<ArgumentOutOfRangeException>();
        }
    }
}
