using EggEncoder.Media;
using EggEncoder.Streaming;
using EggEncoder.Streaming.Hls;
using EggEncoder.Streaming.Mp4;
using EggEncoder.UnitTests.Containers.Flv;
using EggEncoder.UnitTests.Media;
using EggEncoder.UnitTests.Streaming.MpegTs;
using FluentAssertions;
using static EggEncoder.UnitTests.Streaming.Hls.HlsPacketSegmenterTest;

namespace EggEncoder.UnitTests.Streaming.Hls
{
    public class HlsPacketSegmenterUpdateTracksTest
    {
        private const int VideoTrackId = 0;
        private const int AudioTrackId = 1;
        private const int VideoPid = 0x100;

        [Fact]
        public void UpdateTracks_OnAFrameSegmenter_Should_Throw()
        {
            var segmenter = new HlsSegmenter(1, 44100, 2, new InMemoryStreamingSink());

            var act = () => segmenter.UpdateTracks(Tracks());

            act.Should().ThrowExactly<InvalidOperationException>();
        }

        [Fact]
        public void UpdateTracks_AfterComplete_Should_Throw()
        {
            var segmenter = new HlsSegmenter(Tracks(), 2, new InMemoryStreamingSink());
            segmenter.Complete();

            var act = () => segmenter.UpdateTracks(Tracks(videoVersion: 1));

            act.Should().ThrowExactly<InvalidOperationException>();
        }

        [Fact]
        public void UpdateTracks_Null_Should_Throw()
        {
            var segmenter = new HlsSegmenter(Tracks(), 2, new InMemoryStreamingSink());

            var act = () => segmenter.UpdateTracks(null!);

            act.Should().ThrowExactly<ArgumentNullException>();
        }

        public static TheoryData<string, MediaTrackSet> ChangedLayouts => new()
        {
            { "a track removed", new MediaTrackSet([VideoTrack(1)]) },
            { "tracks reordered", new MediaTrackSet([AudioTrack(1), VideoTrack(1)]) },
            { "a track renumbered", new MediaTrackSet([VideoTrack(1) with { TrackId = 5 }, AudioTrack(1)]) },
            { "a codec changed", new MediaTrackSet([VideoTrack(1) with { Codec = MediaCodec.H265, CodecConfiguration = MediaFixtures.Hevc100x60Record }, AudioTrack(1)]) },
            { "a kind changed", new MediaTrackSet([VideoTrack(1) with { Kind = MediaKind.Data }, AudioTrack(1)]) },
            { "a timescale changed", new MediaTrackSet([VideoTrack(1) with { Timescale = 90000 }, AudioTrack(1)]) },
            { "new bytes under the same version", new MediaTrackSet([VideoTrack(0) with { CodecConfiguration = MediaFixtures.AvcBaselineRecord }, AudioTrack(0)]) }
        };

        [Theory]
        [MemberData(nameof(ChangedLayouts))]
        public void UpdateTracks_ChangedLayout_Should_Throw_And_KeepTheCurrentTracks(string reason, MediaTrackSet tracks)
        {
            var sink = new InMemoryStreamingSink();
            var segmenter = new HlsSegmenter(Tracks(), 1, sink);

            var act = () => segmenter.UpdateTracks(tracks);

            act.Should().ThrowExactly<ArgumentException>(reason);
            segmenter.AddPacket(Video(0, key: true));
            segmenter.AddPacket(Video(1000, key: true));
            sink.MediaSegments.Keys.Should().Equal(1L);
        }

        [Theory]
        [InlineData(SegmentFormat.Fmp4)]
        [InlineData(SegmentFormat.MpegTs)]
        public void UpdateTracks_ConfigurationTheContainerRejects_Should_Throw_And_BeIgnored(SegmentFormat format)
        {
            var sink = new InMemoryStreamingSink();
            var segmenter = new HlsSegmenter(Tracks(), 1, sink, format);
            var rejected = format == SegmentFormat.Fmp4
                ? new MediaTrackSet([VideoTrack(1) with { CodecConfiguration = new byte[] { 0x01 } }, AudioTrack(0)])
                : new MediaTrackSet([VideoTrack(0), AudioTrack(1) with { CodecConfiguration = Convert.FromHexString("1240") }]);

            var act = () => segmenter.UpdateTracks(rejected);

            if (format == SegmentFormat.Fmp4)
            {
                act.Should().ThrowExactly<ArgumentException>();
            }
            else
            {
                act.Should().ThrowExactly<NotSupportedException>();
            }

            segmenter.AddPacket(Video(0, key: true));
            segmenter.AddPacket(Audio(10));
            segmenter.Complete();
            sink.MediaSegments.Keys.Should().Equal(1L);
            PlaylistText(sink).Should().NotContain("#EXT-X-DISCONTINUITY");
        }

        [Fact]
        public void UpdateTracks_WithUnchangedVersions_Should_ChangeNothing()
        {
            var sink = new LoggingSink();
            var segmenter = new HlsSegmenter(Tracks(), 1, sink);
            segmenter.AddPacket(Video(0, key: true));

            segmenter.UpdateTracks(Tracks());
            segmenter.AddPacket(Video(40));
            segmenter.AddPacket(Video(1000, key: true));
            segmenter.Complete();

            sink.Inner.InitSegments.Keys.Should().Equal(HlsSegmenter.InitSegmentUri);
            PlaylistDurations(sink.Inner).Should().Equal(1.0, 0.96);
            PlaylistText(sink.Inner).Should().NotContain("#EXT-X-DISCONTINUITY");
        }

        [Fact]
        public void UpdateTracks_Should_WriteNothingUntilTheNextPacket()
        {
            var sink = new LoggingSink();
            var segmenter = new HlsSegmenter(Tracks(), 2, sink);
            segmenter.AddPacket(Video(0, key: true));
            segmenter.AddPacket(Video(40));

            segmenter.UpdateTracks(UpdatedTracks());

            sink.Log.Should().Equal("init:init.mp4");
        }

        [Fact]
        public void AddPacket_OfTheOldVersionAfterAnUpdate_Should_Throw_And_ApplyNothing()
        {
            var sink = new LoggingSink();
            var segmenter = new HlsSegmenter(Tracks(), 2, sink);
            segmenter.AddPacket(Video(0, key: true));
            segmenter.UpdateTracks(UpdatedTracks());

            var act = () => segmenter.AddPacket(Video(40));

            act.Should().ThrowExactly<ArgumentException>();
            sink.Log.Should().Equal("init:init.mp4");
            segmenter.AddPacket(Video(40, version: 1));
            sink.Log.Should().Equal("init:init.mp4", "segment:segment_1.m4s", "playlist");
        }

        [Fact]
        public void UpdateTracks_Fmp4_Should_EndTheSegment_WaitForAKeyframe_And_StartADiscontinuityWithANewInit()
        {
            var sink = new LoggingSink();
            var segmenter = new HlsSegmenter(Tracks(), 2, sink);
            var updated = UpdatedTracks();
            segmenter.AddPacket(Video(0, key: true));
            segmenter.AddPacket(Video(40));
            segmenter.AddPacket(Audio(50));

            segmenter.UpdateTracks(updated);
            segmenter.AddPacket(Audio(60));
            segmenter.AddPacket(Video(100, version: 1));
            segmenter.AddPacket(Video(200, key: true, version: 1));
            segmenter.AddPacket(Audio(210));
            segmenter.AddPacket(Video(2200, key: true, version: 1));
            segmenter.Complete();

            segmenter.DroppedLeadingPacketCount.Should().Be(2);
            sink.Log.Should().Equal(
                "init:init.mp4",
                "segment:segment_1.m4s", "playlist",
                "init:init_2.mp4", "segment:segment_2.m4s", "playlist",
                "segment:segment_3.m4s", "playlist");
            sink.Inner.InitSegments["init_2.mp4"].Should().Equal(FmpInitSegmentWriter.BuildInitSegment(updated));
            sink.Inner.InitSegments.Keys.Should().BeEquivalentTo([HlsSegmenter.InitSegmentUri, "init_2.mp4"], "VOD keeps every init segment");
            PlaylistText(sink.Inner).Should().Be(
                "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:1\n" +
                "#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:0.080000,\nsegment_1.m4s\n" +
                "#EXT-X-DISCONTINUITY\n#EXT-X-MAP:URI=\"init_2.mp4\"\n#EXTINF:2.000000,\nsegment_2.m4s\n" +
                "#EXTINF:2.000000,\nsegment_3.m4s\n#EXT-X-ENDLIST\n");

            var second = ParseFmp4(sink.Inner.MediaSegments[2]);
            second.SequenceNumber.Should().Be(2);
            second.Trafs.Select(t => (t.Mp4TrackId, t.BaseMediaDecodeTime)).Should().Equal((1u, 200L), (2u, 210L));
            second.Trafs[0].FirstSampleFlags.Should().Be(0x02000000u);
            ParseFmp4(sink.Inner.MediaSegments[1]).Trafs.Select(t => t.SampleCount).Should().Equal(2, 1);
        }

        [Fact]
        public void UpdateTracks_MpegTs_Should_MarkTheDiscontinuity_And_CarryTheNewParameterSets()
        {
            var sink = new InMemoryStreamingSink();
            var segmenter = new HlsSegmenter(Tracks(), 2, sink, SegmentFormat.MpegTs);
            segmenter.AddPacket(Video(0, key: true));
            segmenter.AddPacket(Video(40));

            segmenter.UpdateTracks(UpdatedTracks());
            segmenter.AddPacket(Video(1000, key: true, version: 1));
            segmenter.Complete();

            PlaylistText(sink).Should().Contain("segment_1.ts\n#EXT-X-DISCONTINUITY\n#EXTINF:").And.NotContain("#EXT-X-MAP");
            sink.InitSegment.Should().BeNull();
            var keyframe = MpegTsTestReader.ReadPes(MpegTsTestReader.ReadPackets(sink.MediaSegments[2]), VideoPid).Single();
            ContainsSequence(keyframe.Es, ParameterSet(MediaFixtures.AvcBaselineRecord)).Should().BeTrue();
            ContainsSequence(keyframe.Es, ParameterSet(FlvTestBuilder.AvcC)).Should().BeFalse();
            var lastFirstSegmentCounter = MpegTsTestReader.ReadPackets(sink.MediaSegments[1]).Last(p => p.Pid == VideoPid).ContinuityCounter;
            MpegTsTestReader.ReadPackets(sink.MediaSegments[2]).First(p => p.Pid == VideoPid && p.PayloadUnitStart).ContinuityCounter
                .Should().Be((lastFirstSegmentCounter + 1) & 0x0F);
        }

        [Fact]
        public void UpdateTracks_BeforeTheFirstSegment_Should_UseTheNewInit_WithoutADiscontinuity()
        {
            var sink = new InMemoryStreamingSink();
            var segmenter = new HlsSegmenter(Tracks(), 2, sink);

            segmenter.UpdateTracks(UpdatedTracks());
            segmenter.AddPacket(Video(0, key: true, version: 1));
            segmenter.Complete();

            PlaylistText(sink).Should().Contain("#EXT-X-MAP:URI=\"init_2.mp4\"\n#EXTINF:")
                .And.NotContain("init.mp4").And.NotContain("#EXT-X-DISCONTINUITY");
        }

        [Fact]
        public void UpdateTracks_TwiceBeforeTheNextPacket_Should_WriteOnlyTheLatestInit()
        {
            var sink = new InMemoryStreamingSink();
            var segmenter = new HlsSegmenter(Tracks(), 2, sink);
            var latest = new MediaTrackSet([VideoTrack(2) with { CodecConfiguration = MediaFixtures.Avc1080pRecord }, AudioTrack(0)]);
            segmenter.AddPacket(Video(0, key: true));

            segmenter.UpdateTracks(UpdatedTracks());
            segmenter.UpdateTracks(UpdatedTracks());
            segmenter.UpdateTracks(latest);
            var stale = () => segmenter.AddPacket(Video(1000, key: true, version: 1));
            stale.Should().ThrowExactly<ArgumentException>();
            segmenter.AddPacket(Video(1000, key: true, version: 2));
            segmenter.Complete();

            sink.InitSegments.Keys.Should().BeEquivalentTo([HlsSegmenter.InitSegmentUri, "init_2.mp4"]);
            sink.InitSegments["init_2.mp4"].Should().Equal(FmpInitSegmentWriter.BuildInitSegment(latest));
        }

        [Fact]
        public void Complete_WithAnUpdateNotYetApplied_Should_EndWithTheOldConfiguration()
        {
            var sink = new InMemoryStreamingSink();
            var segmenter = new HlsSegmenter(Tracks(), 2, sink);
            segmenter.AddPacket(Video(0, key: true));
            segmenter.AddPacket(Video(40));

            segmenter.UpdateTracks(UpdatedTracks());
            segmenter.Complete();

            sink.MediaSegments.Keys.Should().Equal(1L);
            sink.InitSegments.Keys.Should().Equal(HlsSegmenter.InitSegmentUri);
            PlaylistText(sink).Should().NotContain("#EXT-X-DISCONTINUITY");
        }

        [Fact]
        public void LiveMode_Should_DeleteAnInitNoListedSegmentNeeds_And_CountDiscontinuitiesThatLeaveTheWindow()
        {
            var sink = new LoggingSink();
            var segmenter = new HlsSegmenter(Tracks(), 1, sink, liveWindowSize: 1);
            segmenter.AddPacket(Video(0, key: true));
            segmenter.AddPacket(Video(1000, key: true));

            segmenter.UpdateTracks(UpdatedTracks());
            segmenter.AddPacket(Video(1500, key: true, version: 1));
            segmenter.AddPacket(Video(2500, key: true, version: 1));

            sink.Log.Should().EndWith(["init:init_2.mp4", "segment:segment_3.m4s", "delete:segment_2.m4s", "delete-init:init.mp4", "playlist"]);
            PlaylistText(sink.Inner).Should().Be(
                "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:1\n#EXT-X-MEDIA-SEQUENCE:3\n" +
                "#EXT-X-MAP:URI=\"init_2.mp4\"\n#EXT-X-DISCONTINUITY\n#EXTINF:1.000000,\nsegment_3.m4s\n");

            segmenter.AddPacket(Video(3500, key: true, version: 1));
            segmenter.Complete();

            sink.Inner.InitSegments.Keys.Should().Equal("init_2.mp4");
            PlaylistText(sink.Inner).Should().Be(
                "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:1\n#EXT-X-MEDIA-SEQUENCE:5\n#EXT-X-DISCONTINUITY-SEQUENCE:1\n" +
                "#EXT-X-MAP:URI=\"init_2.mp4\"\n#EXTINF:1.000000,\nsegment_5.m4s\n#EXT-X-ENDLIST\n");
        }

        [Fact]
        public void LiveMode_Should_KeepAnOldInit_WhileASegmentInTheWindowStillNeedsIt()
        {
            var sink = new LoggingSink();
            var segmenter = new HlsSegmenter(Tracks(), 1, sink, liveWindowSize: 2);
            segmenter.AddPacket(Video(0, key: true));
            segmenter.AddPacket(Video(1000, key: true));
            segmenter.AddPacket(Video(2000, key: true));

            segmenter.UpdateTracks(UpdatedTracks());
            segmenter.AddPacket(Video(3000, key: true, version: 1));
            segmenter.AddPacket(Video(4000, key: true, version: 1));

            sink.Log.Should().NotContain("delete-init:init.mp4");
            PlaylistText(sink.Inner).Should().Be(
                "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:1\n#EXT-X-MEDIA-SEQUENCE:3\n" +
                "#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:1.000000,\nsegment_3.m4s\n" +
                "#EXT-X-DISCONTINUITY\n#EXT-X-MAP:URI=\"init_2.mp4\"\n#EXTINF:1.000000,\nsegment_4.m4s\n");

            segmenter.AddPacket(Video(5000, key: true, version: 1));

            sink.Log.Should().EndWith(["delete:segment_3.m4s", "delete-init:init.mp4", "playlist"]);
            sink.Inner.InitSegments.Keys.Should().Equal("init_2.mp4");
        }

        [Fact]
        public void AddPacket_SinkFailingOnTheNewInit_Should_Throw_DiscardTheSegment_And_RetryTheInitAtTheNextCut()
        {
            var sink = new LoggingSink { FailingInitSegmentName = "init_2.mp4" };
            var segmenter = new HlsSegmenter(Tracks(), 1, sink);
            segmenter.AddPacket(Video(0, key: true));
            segmenter.UpdateTracks(UpdatedTracks());
            segmenter.AddPacket(Video(1000, key: true, version: 1));

            var act = () => segmenter.AddPacket(Video(2000, key: true, version: 1));

            act.Should().ThrowExactly<IOException>();
            sink.FailingInitSegmentName = null;
            segmenter.AddPacket(Video(3000, key: true, version: 1));
            segmenter.Complete();

            sink.Inner.MediaSegments.Keys.Should().Equal(1L, 2L, 3L);
            FirstVideoDecodeTime(sink.Inner.MediaSegments[2]).Should().Be(2000);
            PlaylistText(sink.Inner).Should().Contain("segment_1.m4s\n#EXT-X-DISCONTINUITY\n#EXT-X-MAP:URI=\"init_2.mp4\"\n#EXTINF:1.000000,\nsegment_2.m4s\n");
        }

        [Fact]
        public void AddPacket_SegmentTheMuxerRejectsWhenAnUpdateEndsIt_Should_Throw_And_StillApplyTheUpdate()
        {
            var sink = new InMemoryStreamingSink();
            var segmenter = new HlsSegmenter(Tracks(), 1, sink, SegmentFormat.MpegTs);
            segmenter.AddPacket(Video(0, key: true));
            segmenter.AddPacket(Video(40) with { Payload = new byte[] { 0, 0, 0, 9, 0x41 } });
            segmenter.UpdateTracks(UpdatedTracks());

            var act = () => segmenter.AddPacket(Video(1000, key: true, version: 1));

            act.Should().ThrowExactly<ArgumentException>();
            segmenter.AddPacket(Video(2000, key: true, version: 1));
            segmenter.Complete();

            sink.MediaSegments.Keys.Should().Equal(1L, 2L);
            MpegTsTestReader.ReadPes(MpegTsTestReader.ReadPackets(sink.MediaSegments[1]), VideoPid).Single().DecodeTime.Should().Be(1000 * 90);
            PlaylistText(sink).Should().NotContain("#EXT-X-DISCONTINUITY", "no segment before the change made it into the playlist");
        }

        [Theory]
        [InlineData(SegmentFormat.Fmp4)]
        [InlineData(SegmentFormat.MpegTs)]
        public void AddPacket_DroppedPacketThatAppliesAnUpdate_Should_StillThrowTheCutFailure(SegmentFormat format)
        {
            var inner = new InMemoryStreamingSink();
            var sink = new FailingSink(inner);
            var segmenter = new HlsSegmenter(Tracks(), 1, sink, format);
            segmenter.AddPacket(Video(0, key: true));
            segmenter.AddPacket(Video(1000, key: true));
            segmenter.AddPacket(Video(1040));
            segmenter.UpdateTracks(UpdatedTracks());
            sink.FailMediaSegments = true;

            var act = () => segmenter.AddPacket(Audio(1050));

            act.Should().ThrowExactly<IOException>();
            segmenter.DroppedLeadingPacketCount.Should().Be(1);
            sink.FailMediaSegments = false;
            segmenter.AddPacket(Video(2000, key: true, version: 1));
            segmenter.Complete();
            inner.MediaSegments.Keys.Should().Equal(1L, 2L);
        }

        [Fact]
        public void UpdateTracks_ReusingTheAppliedVersionWithNewBytes_Should_Throw_And_KeepThePendingUpdate()
        {
            var sink = new InMemoryStreamingSink();
            var segmenter = new HlsSegmenter(Tracks(), 1, sink);
            segmenter.AddPacket(Video(0, key: true));
            segmenter.UpdateTracks(UpdatedTracks());

            var act = () => segmenter.UpdateTracks(new MediaTrackSet([VideoTrack(0) with { CodecConfiguration = MediaFixtures.AvcBaselineRecord }, AudioTrack(0)]));

            act.Should().ThrowExactly<ArgumentException>();
            segmenter.AddPacket(Video(1000, key: true, version: 1));
            segmenter.AddPacket(Video(2000, key: true, version: 1));
            sink.MediaSegments.Keys.Should().Equal(1L, 2L);
            sink.InitSegments["init_2.mp4"].Should().Equal(FmpInitSegmentWriter.BuildInitSegment(UpdatedTracks()));
        }

        [Fact]
        public async Task HlsMediaPacketSink_UpdateTracks_Should_ForwardToTheSegmenter()
        {
            var sink = new InMemoryStreamingSink();
            await using var packetSink = new HlsMediaPacketSink(sink, 1);
            var beforeOpen = () => packetSink.UpdateTracks(UpdatedTracks());
            beforeOpen.Should().ThrowExactly<InvalidOperationException>();

            await packetSink.OpenAsync(Tracks());
            await packetSink.WriteAsync(Video(0, key: true));
            packetSink.UpdateTracks(UpdatedTracks());
            await packetSink.WriteAsync(Video(1000, key: true, version: 1));
            await packetSink.CompleteAsync();

            PlaylistText(sink).Should().Contain("#EXT-X-DISCONTINUITY\n#EXT-X-MAP:URI=\"init_2.mp4\"");
        }

        [Fact]
        public void Ffmpeg_OnAPlaylistWithAConfigurationChange_Should_Report_And_Decode_Both_Streams()
        {
            if (Environment.GetEnvironmentVariable("EGGENCODER_FFMPEG_ORACLE") != "1")
            {
                return;
            }

            var capture = FiveSecondCapture();
            var tracks = capture.Demuxer.Tracks!;
            var bumped = new MediaTrackSet(tracks.Tracks.Select(t => t with { ConfigVersion = t.ConfigVersion + 1 }).ToArray());
            foreach (var format in new[] { SegmentFormat.Fmp4, SegmentFormat.MpegTs })
            {
                var sink = new InMemoryStreamingSink();
                var segmenter = new HlsSegmenter(tracks, 1, sink, format);
                var updated = false;
                foreach (var packet in capture.Packets)
                {
                    if (!updated && packet.Dts >= 2500)
                    {
                        segmenter.UpdateTracks(bumped);
                        updated = true;
                    }

                    segmenter.AddPacket(updated ? packet with { ConfigVersion = packet.ConfigVersion + 1 } : packet);
                }

                segmenter.Complete();
                PlaylistText(sink).Should().Contain("#EXT-X-DISCONTINUITY\n");

                var directory = Directory.CreateTempSubdirectory("eggencoder-hls-");
                try
                {
                    var playlistPath = WriteToDirectory(sink, directory.FullName, format);

                    var (probeExit, probeOutput, _) = Run("ffprobe", $"-v error -show_entries stream=codec_name,width,height,sample_rate,channels -of compact=p=0 \"{playlistPath}\"");
                    probeExit.Should().Be(0);
                    probeOutput.Should().Contain("codec_name=h264|width=64|height=64");
                    probeOutput.Should().Contain("codec_name=aac|sample_rate=44100|channels=1");

                    var (decodeExit, _, decodeErrors) = Run("ffmpeg", $"-v error -i \"{playlistPath}\" -f null -");
                    decodeExit.Should().Be(0);
                    decodeErrors.Should().BeEmpty();
                }
                finally
                {
                    directory.Delete(recursive: true);
                }
            }
        }

        private static MediaTrackSet Tracks(int videoVersion = 0) => new([VideoTrack(videoVersion), AudioTrack(0)]);

        private static MediaTrackSet UpdatedTracks() =>
            new([VideoTrack(1) with { CodecConfiguration = MediaFixtures.AvcBaselineRecord }, AudioTrack(0)]);

        private static MediaTrackInfo VideoTrack(int version) =>
            new(VideoTrackId, MediaKind.Video, MediaCodec.H264, 1000, FlvTestBuilder.AvcC, version, width: 64, height: 64);

        private static MediaTrackInfo AudioTrack(int version) =>
            new(AudioTrackId, MediaKind.Audio, MediaCodec.Aac, 1000, FlvTestBuilder.AacConfig, version, 44100, 1);

        private static MediaPacket Video(long dts, bool key = false, int version = 0) =>
            new(VideoTrackId, dts, dts, key, version, new byte[] { 0, 0, 0, 1, (byte)(key ? 0x65 : 0x41) });

        private static MediaPacket Audio(long dts) => new(AudioTrackId, dts, dts, true, 0, new byte[] { 0x21, 0x10 });

        private static long FirstVideoDecodeTime(byte[] segment) => ParseFmp4(segment).Trafs[0].BaseMediaDecodeTime;

        private static byte[] ParameterSet(byte[] avcC) =>
            [0, 0, 0, 1, .. AvcDecoderConfigurationRecord.Parse(avcC).SequenceParameterSets[0].Span];

        private static bool ContainsSequence(byte[] haystack, byte[] needle) =>
            haystack.AsSpan().IndexOf(needle) >= 0;

        private sealed class FailingSink(InMemoryStreamingSink inner) : IStreamingSink
        {
            public bool FailMediaSegments { get; set; }

            public void WriteInitSegment(ReadOnlySpan<byte> bytes) => inner.WriteInitSegment(bytes);

            public void WriteMediaSegment(long sequenceNumber, ReadOnlySpan<byte> bytes)
            {
                if (FailMediaSegments)
                {
                    throw new IOException("Upload failed");
                }

                inner.WriteMediaSegment(sequenceNumber, bytes);
            }

            public void WritePlaylist(ReadOnlySpan<byte> utf8Bytes) => inner.WritePlaylist(utf8Bytes);

            public void DeleteMediaSegment(long sequenceNumber) => inner.DeleteMediaSegment(sequenceNumber);

            public string WriteInitSegment(string fileName, ReadOnlySpan<byte> bytes) => inner.WriteInitSegment(fileName, bytes);
        }

        private sealed class LoggingSink : IStreamingSink
        {
            public InMemoryStreamingSink Inner { get; } = new();

            public List<string> Log { get; } = [];

            public string? FailingInitSegmentName { get; set; }

            public void WriteInitSegment(ReadOnlySpan<byte> bytes) => throw new InvalidOperationException("The segmenter names every init segment.");

            public void WriteMediaSegment(long sequenceNumber, ReadOnlySpan<byte> bytes) => throw new InvalidOperationException("The segmenter names every media segment.");

            public void WritePlaylist(ReadOnlySpan<byte> utf8Bytes)
            {
                Log.Add("playlist");
                Inner.WritePlaylist(utf8Bytes);
            }

            public void DeleteMediaSegment(long sequenceNumber) => throw new InvalidOperationException("The segmenter passes every URI.");

            public string WriteInitSegment(string fileName, ReadOnlySpan<byte> bytes)
            {
                if (fileName == FailingInitSegmentName)
                {
                    throw new IOException("Upload failed");
                }

                Log.Add($"init:{fileName}");
                return Inner.WriteInitSegment(fileName, bytes);
            }

            public string WriteMediaSegment(long sequenceNumber, string fileName, ReadOnlySpan<byte> bytes)
            {
                Log.Add($"segment:{fileName}");
                Inner.WriteMediaSegment(sequenceNumber, bytes);
                return fileName;
            }

            public void DeleteMediaSegment(long sequenceNumber, string uri)
            {
                Log.Add($"delete:{uri}");
                Inner.DeleteMediaSegment(sequenceNumber);
            }

            public void DeleteInitSegment(string uri)
            {
                Log.Add($"delete-init:{uri}");
                Inner.DeleteInitSegment(uri);
            }
        }
    }
}
