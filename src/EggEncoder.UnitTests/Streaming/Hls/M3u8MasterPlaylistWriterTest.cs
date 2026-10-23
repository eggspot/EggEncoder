using EggEncoder.Media;
using EggEncoder.Streaming.Hls;
using EggEncoder.UnitTests.Containers.Flv;
using EggEncoder.UnitTests.Media;
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

        [Fact]
        public void BuildCodecsString_H264AndAac_Should_ListVideoFirst_FromTheSpsAndAudioObjectType()
        {
            var tracks = new MediaTrackSet([Aac(0, FlvTestBuilder.AacConfig), Video(1, MediaCodec.H264, MediaFixtures.Avc1080pRecord)]);

            M3u8MasterPlaylistWriter.BuildCodecsString(tracks).Should().Be("avc1.640028,mp4a.40.2");
        }

        [Fact]
        public void BuildCodecsString_H265_Should_UseTheHvc1SampleEntry()
        {
            var tracks = new MediaTrackSet([Video(0, MediaCodec.H265, MediaFixtures.Hevc1080pRecord)]);

            M3u8MasterPlaylistWriter.BuildCodecsString(tracks).Should().Be("hvc1.1.6.L120.90");
        }

        [Fact]
        public void BuildCodecsString_ExplicitHeAac_Should_ReportObjectType5()
        {
            var tracks = new MediaTrackSet([Aac(0, [0x2B, 0x91, 0x88])]);

            M3u8MasterPlaylistWriter.BuildCodecsString(tracks).Should().Be("mp4a.40.5");
        }

        [Fact]
        public void BuildCodecsString_RepeatedCodec_Should_AppearOnce()
        {
            var tracks = new MediaTrackSet([Aac(0, FlvTestBuilder.AacConfig), Aac(1, FlvTestBuilder.AacConfig)]);

            M3u8MasterPlaylistWriter.BuildCodecsString(tracks).Should().Be("mp4a.40.2");
        }

        [Fact]
        public void BuildCodecsString_FlvFixtureTracks_Should_MatchTheirSps()
        {
            var tracks = FlvDemuxerTest.Demux(File.ReadAllBytes(FlvDemuxerTest.H264AacFixture)).Demuxer.Tracks!;

            M3u8MasterPlaylistWriter.BuildCodecsString(tracks).Should().Be("avc1.4D400A,mp4a.40.2");
        }

        [Fact]
        public void BuildCodecsString_Null_Should_Throw()
        {
            var act = () => M3u8MasterPlaylistWriter.BuildCodecsString(null!);

            act.Should().ThrowExactly<ArgumentNullException>();
        }

        [Fact]
        public void BuildCodecsString_EmptySet_Should_Throw()
        {
            var act = () => M3u8MasterPlaylistWriter.BuildCodecsString(new MediaTrackSet([]));

            act.Should().ThrowExactly<ArgumentException>().WithParameterName("tracks");
        }

        [Fact]
        public void BuildCodecsString_UnsupportedCodec_Should_Throw()
        {
            var mp3 = new MediaTrackInfo(0, MediaKind.Audio, MediaCodec.Mp3, 1000, ReadOnlyMemory<byte>.Empty, 0, 44100, 2);

            var act = () => M3u8MasterPlaylistWriter.BuildCodecsString(new MediaTrackSet([mp3]));

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Theory]
        [InlineData(MediaCodec.H264)]
        [InlineData(MediaCodec.H265)]
        [InlineData(MediaCodec.Aac)]
        public void BuildCodecsString_UnreadableConfiguration_Should_Throw(MediaCodec codec)
        {
            var track = codec == MediaCodec.Aac ? Aac(0, [0x12]) : Video(0, codec, [0x01, 0x64]);

            var act = () => M3u8MasterPlaylistWriter.BuildCodecsString(new MediaTrackSet([track]));

            act.Should().ThrowExactly<ArgumentException>().WithParameterName("tracks").WithInnerException<InvalidDataException>();
        }

        private static MediaTrackInfo Video(int trackId, MediaCodec codec, byte[] record) =>
            new(trackId, MediaKind.Video, codec, 1000, record, 0);

        private static MediaTrackInfo Aac(int trackId, byte[] config) =>
            new(trackId, MediaKind.Audio, MediaCodec.Aac, 1000, config, 0);
    }
}
