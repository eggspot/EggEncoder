using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using EggEncoder.Codecs.Mov;
using EggEncoder.Media;
using EggEncoder.Streaming;
using EggEncoder.Streaming.Hls;
using EggEncoder.Streaming.Mp4;
using EggEncoder.UnitTests.Containers.Flv;
using EggEncoder.UnitTests.Streaming.MpegTs;
using FluentAssertions;

namespace EggEncoder.UnitTests.Streaming.Hls
{
    public class HlsPacketSegmenterTest
    {
        internal static readonly string FiveSecondFixture = Path.GetFullPath("Streaming/Hls/h264_aac_5s.flv");

        private const int VideoTrackId = 0;
        private const int AudioTrackId = 1;
        private const int FirstTrackPid = 0x100;
        private const int VideoPid = FirstTrackPid;
        private const int AudioPid = FirstTrackPid + 1;

        [Fact]
        public void Constructor_Fmp4_Should_WriteTheTrackSetsInitSegment()
        {
            var sink = new InMemoryStreamingSink();
            var tracks = Tracks();

            _ = new HlsSegmenter(tracks, targetDurationSeconds: 2, sink);

            sink.InitSegment.Should().Equal(FmpInitSegmentWriter.BuildInitSegment(tracks));
            sink.MediaSegments.Should().BeEmpty();
            sink.Playlist.Should().BeNull();
        }

        [Fact]
        public void Constructor_MpegTs_Should_NotWriteAnInitSegment()
        {
            var sink = new InMemoryStreamingSink();

            _ = new HlsSegmenter(Tracks(), targetDurationSeconds: 2, sink, SegmentFormat.MpegTs);

            sink.InitSegment.Should().BeNull();
        }

        [Fact]
        public void Constructor_NullTracks_Should_Throw()
        {
            var act = () => new HlsSegmenter((MediaTrackSet)null!, 2, new InMemoryStreamingSink());

            act.Should().ThrowExactly<ArgumentNullException>();
        }

        [Fact]
        public void Constructor_NullSink_Should_Throw()
        {
            var act = () => new HlsSegmenter(Tracks(), 2, null!);

            act.Should().ThrowExactly<ArgumentNullException>();
        }

        [Fact]
        public void Constructor_NonPositiveTargetDuration_Should_Throw()
        {
            var act = () => new HlsSegmenter(Tracks(), 0, new InMemoryStreamingSink());

            act.Should().ThrowExactly<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Constructor_LiveWindowBelowOne_Should_Throw()
        {
            var act = () => new HlsSegmenter(Tracks(), 2, new InMemoryStreamingSink(), liveWindowSize: 0);

            act.Should().ThrowExactly<ArgumentOutOfRangeException>();
        }

        [Theory]
        [InlineData(SegmentFormat.Fmp4)]
        [InlineData(SegmentFormat.MpegTs)]
        public void Constructor_EmptyTrackSet_Should_Throw_And_WriteNothing(SegmentFormat format)
        {
            var sink = new InMemoryStreamingSink();

            var act = () => new HlsSegmenter(new MediaTrackSet([]), 2, sink, format);

            act.Should().ThrowExactly<ArgumentException>();
            sink.InitSegment.Should().BeNull();
        }

        [Theory]
        [InlineData(SegmentFormat.Fmp4)]
        [InlineData(SegmentFormat.MpegTs)]
        public void Constructor_UnsupportedCodec_Should_Throw(SegmentFormat format)
        {
            var mp3 = new MediaTrackInfo(2, MediaKind.Audio, MediaCodec.Mp3, 1000, ReadOnlyMemory<byte>.Empty, 0, 44100, 2);

            var act = () => new HlsSegmenter(new MediaTrackSet([VideoTrack(), mp3]), 2, new InMemoryStreamingSink(), format);

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void AddPacket_OnAFrameSegmenter_Should_Throw()
        {
            var segmenter = new HlsSegmenter(1, 44100, 2, new InMemoryStreamingSink());

            var act = () => segmenter.AddPacket(Audio(0));

            act.Should().ThrowExactly<InvalidOperationException>();
        }

        [Fact]
        public void AddFrame_OnAPacketSegmenter_Should_Throw()
        {
            var segmenter = new HlsSegmenter(Tracks(), 2, new InMemoryStreamingSink());

            var act = () => segmenter.AddFrame(new EncodedAudioFrame(new byte[] { 1 }, 1024, 0));

            act.Should().ThrowExactly<InvalidOperationException>();
        }

        [Fact]
        public void AddPacket_AfterComplete_Should_Throw()
        {
            var segmenter = new HlsSegmenter(Tracks(), 2, new InMemoryStreamingSink());
            segmenter.Complete();

            var act = () => segmenter.AddPacket(Video(0, key: true));

            act.Should().ThrowExactly<InvalidOperationException>();
        }

        public static TheoryData<string, MediaPacket> InvalidPackets => new()
        {
            { "unknown track", new MediaPacket(7, 1000, 1000, true, 0, new byte[] { 1 }) },
            { "config version", Video(1000, key: true) with { ConfigVersion = 1 } },
            { "negative DTS", Video(-40) with { Pts = 0 } },
            { "PTS before DTS", Video(1000) with { Pts = 960 } },
            { "DTS not increasing", Video(40) },
        };

        [Theory]
        [MemberData(nameof(InvalidPackets))]
        public void AddPacket_InvalidPacket_Should_Throw_And_AddNothing(string reason, MediaPacket packet)
        {
            var sink = new InMemoryStreamingSink();
            var segmenter = new HlsSegmenter(Tracks(), 1, sink);
            segmenter.AddPacket(Video(0, key: true));
            segmenter.AddPacket(Video(40));

            var act = () => segmenter.AddPacket(packet);

            act.Should().ThrowExactly<ArgumentException>(reason);
            sink.MediaSegments.Should().BeEmpty();
            segmenter.AddPacket(Video(1000, key: true));
            segmenter.Complete();
            SegmentPacketCounts(sink).Should().Equal([2, 1]);
        }

        [Fact]
        public void AddPacket_BeforeFirstVideoKeyframe_Should_DropAndCountEveryPacket()
        {
            var sink = new InMemoryStreamingSink();
            var segmenter = new HlsSegmenter(Tracks(), 2, sink);

            segmenter.AddPacket(Audio(0));
            segmenter.AddPacket(Video(0));
            segmenter.AddPacket(Audio(23));
            segmenter.AddPacket(Video(40, key: true));
            segmenter.AddPacket(Audio(46));
            segmenter.Complete();

            segmenter.DroppedLeadingPacketCount.Should().Be(3);
            var segment = ParseFmp4(sink.MediaSegments[1]);
            segment.Trafs.Select(t => (t.Mp4TrackId, t.BaseMediaDecodeTime, t.SampleCount)).Should().Equal((1u, 40L, 1), (2u, 46L, 1));
        }

        [Fact]
        public void AddPacket_DroppedPacket_Should_StillAdvanceItsTracksLastDts()
        {
            var segmenter = new HlsSegmenter(Tracks(), 2, new InMemoryStreamingSink());
            segmenter.AddPacket(Audio(100));

            var act = () => segmenter.AddPacket(Audio(100));

            act.Should().ThrowExactly<ArgumentException>();
            segmenter.DroppedLeadingPacketCount.Should().Be(1);
        }

        [Fact]
        public void AddPacket_KeyframeBelowTarget_Should_NotCut()
        {
            var sink = new InMemoryStreamingSink();
            var segmenter = new HlsSegmenter(Tracks(), 2, sink);

            segmenter.AddPacket(Video(0, key: true));
            segmenter.AddPacket(Video(1960, key: true));

            sink.MediaSegments.Should().BeEmpty();
            sink.Playlist.Should().BeNull();
        }

        [Fact]
        public void AddPacket_NonKeyframePastTarget_Should_NotCut()
        {
            var sink = new InMemoryStreamingSink();
            var segmenter = new HlsSegmenter(Tracks(), 2, sink);

            segmenter.AddPacket(Video(0, key: true));
            segmenter.AddPacket(Video(5000));
            segmenter.AddPacket(Audio(5010));

            sink.MediaSegments.Should().BeEmpty();
        }

        [Fact]
        public void AddPacket_KeyframeReachingTarget_Should_CutBeforeIt_And_StartTheNextSegmentWithIt()
        {
            var sink = new InMemoryStreamingSink();
            var segmenter = new HlsSegmenter(Tracks(), 2, sink);

            segmenter.AddPacket(Video(0, key: true, pts: 80));
            segmenter.AddPacket(Video(40, pts: 160));
            segmenter.AddPacket(Audio(50));
            segmenter.AddPacket(Video(2000, key: true, pts: 2080));

            sink.MediaSegments.Keys.Should().Equal(1L);
            var segment = ParseFmp4(sink.MediaSegments[1]);
            segment.Trafs[0].SampleCount.Should().Be(2);
            segment.Trafs[0].Durations.Should().Equal(40u, 1960u);
            PlaylistDurations(sink).Should().Equal(2.0);
            PlaylistText(sink).Should().NotContain("#EXT-X-ENDLIST");

            segmenter.AddPacket(Audio(2010));
            segmenter.Complete();

            var second = ParseFmp4(sink.MediaSegments[2]);
            second.Trafs.Select(t => (t.Mp4TrackId, t.BaseMediaDecodeTime)).Should().Equal((1u, 2000L), (2u, 2010L));
            second.Trafs[0].FirstSampleFlags.Should().Be(0x02000000u);
        }

        [Fact]
        public void Complete_FinalSegment_Should_LastUntilTheLatestPtsPlusOneFrame()
        {
            var sink = new InMemoryStreamingSink();
            var segmenter = new HlsSegmenter(Tracks(), 10, sink);

            segmenter.AddPacket(Video(0, key: true, pts: 80));
            segmenter.AddPacket(Video(40, pts: 200));
            segmenter.AddPacket(Video(80, pts: 120));
            segmenter.Complete();

            PlaylistDurations(sink).Should().Equal(0.16);
            ParseFmp4(sink.MediaSegments[1]).Trafs[0].Durations.Should().Equal(40u, 40u, 40u);
            PlaylistText(sink).Should().EndWith("#EXT-X-ENDLIST\n");
        }

        [Fact]
        public void Complete_SingleVideoFrame_Should_UseADefaultFrameDuration()
        {
            var sink = new InMemoryStreamingSink();
            var segmenter = new HlsSegmenter(Tracks(), 2, sink);

            segmenter.AddPacket(Video(0, key: true));
            segmenter.Complete();

            PlaylistDurations(sink).Should().Equal(0.033);
        }

        [Fact]
        public void Complete_SingleAudioFrame_Should_UseOneAacFrameAsTheDefaultDuration()
        {
            var sink = new InMemoryStreamingSink();
            var audioOnly = new MediaTrackInfo(AudioTrackId, MediaKind.Audio, MediaCodec.Aac, 44100, FlvTestBuilder.AacConfig, 0);
            var segmenter = new HlsSegmenter(new MediaTrackSet([audioOnly]), 2, sink);

            segmenter.AddPacket(new MediaPacket(AudioTrackId, 0, 0, true, 0, new byte[] { 0x21, 0x10 }));
            segmenter.Complete();

            PlaylistDurations(sink).Should().Equal(0.02322);
            ParseFmp4(sink.MediaSegments[1]).Trafs[0].Durations.Should().Equal(1024u);
        }

        [Fact]
        public void Complete_AfterACut_Should_UseTheCutTracksLastDeltaForTheFinalSegment()
        {
            var sink = new InMemoryStreamingSink();
            var segmenter = new HlsSegmenter(Tracks(), 1, sink);

            segmenter.AddPacket(Video(0, key: true));
            segmenter.AddPacket(Video(500));
            segmenter.AddPacket(Video(1000, key: true));
            segmenter.Complete();

            PlaylistDurations(sink).Should().Equal(1.0, 0.5);
        }

        [Fact]
        public void Complete_WithNoKeptPackets_Should_WriteAnEmptyEndedPlaylist()
        {
            var sink = new InMemoryStreamingSink();
            var segmenter = new HlsSegmenter(Tracks(), 2, sink);
            segmenter.AddPacket(Audio(0));

            segmenter.Complete();
            segmenter.Complete();

            sink.MediaSegments.Should().BeEmpty();
            PlaylistText(sink).Should().Be("#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:1\n#EXT-X-MAP:URI=\"init.mp4\"\n#EXT-X-ENDLIST\n");
        }

        [Fact]
        public void AddPacket_AudioOnly_Should_CutAtTheFirstPacketReachingTheTarget()
        {
            var sink = new InMemoryStreamingSink();
            var audioOnly = new MediaTrackInfo(AudioTrackId, MediaKind.Audio, MediaCodec.Aac, 44100, FlvTestBuilder.AacConfig, 0);
            var segmenter = new HlsSegmenter(new MediaTrackSet([audioOnly]), 1, sink, SegmentFormat.MpegTs);

            for (var i = 0; i < 100; i++)
            {
                segmenter.AddPacket(new MediaPacket(AudioTrackId, i * 1024L, i * 1024L, IsKeyframe: false, 0, new byte[] { 0x21, 0x10 }));
            }

            segmenter.Complete();

            segmenter.DroppedLeadingPacketCount.Should().Be(0);
            PlaylistDurations(sink).Should().Equal(1.021678, 1.021678, 0.278639);
            sink.MediaSegments.Values.Select(s => MpegTsTestReader.ReadPes(MpegTsTestReader.ReadPackets(s), FirstTrackPid).Count).Should().Equal(44, 44, 12);
        }

        [Fact]
        public void AddPacket_AudioOnlyAac_Should_NotDropPacketsMarkedAsNonKeyframes()
        {
            var sink = new InMemoryStreamingSink();
            var segmenter = new HlsSegmenter(new MediaTrackSet([AudioTrack()]), 2, sink);

            segmenter.AddPacket(Audio(0) with { IsKeyframe = false });
            segmenter.Complete();

            segmenter.DroppedLeadingPacketCount.Should().Be(0);
            sink.MediaSegments.Should().ContainSingle();
        }

        [Fact]
        public void Playlist_TargetDuration_Should_CoverTheLongestGop()
        {
            var sink = new InMemoryStreamingSink();
            var segmenter = new HlsSegmenter(Tracks(), 1, sink);

            segmenter.AddPacket(Video(0, key: true));
            segmenter.AddPacket(Video(2500, key: true));
            segmenter.AddPacket(Video(3000, key: true));
            segmenter.AddPacket(Video(4000, key: true));

            PlaylistText(sink).Should().Contain("#EXT-X-TARGETDURATION:3\n");
            PlaylistDurations(sink).Should().Equal(2.5, 1.5);
        }

        [Fact]
        public void LiveMode_Should_KeepTheWindow_And_DeleteAgedOutSegments()
        {
            var sink = new RecordingSink();
            var segmenter = new HlsSegmenter(Tracks(), 1, sink, liveWindowSize: 2, SegmentFormat.MpegTs);

            foreach (var packet in FixturePackets())
            {
                segmenter.AddPacket(packet);
            }

            segmenter.Complete();

            sink.Written.Should().Equal(1L, 2L, 3L, 4L, 5L);
            sink.Deleted.Should().Equal(1L, 2L, 3L);
            var playlist = Encoding.UTF8.GetString(sink.Playlist!);
            playlist.Should().Contain("#EXT-X-MEDIA-SEQUENCE:4\n").And.Contain("segment_4.ts\n").And.Contain("segment_5.ts\n").And.NotContain("segment_3.ts");
            playlist.Should().NotContain("#EXT-X-MAP").And.EndWith("#EXT-X-ENDLIST\n");
        }

        [Fact]
        public void Fixture_Fmp4_Should_StartEverySegmentAtAKeyframe_With_ContinuousDecodeTimes()
        {
            var capture = FiveSecondCapture();
            var sink = new InMemoryStreamingSink();
            var segmenter = new HlsSegmenter(capture.Demuxer.Tracks!, 2, sink);

            foreach (var packet in capture.Packets)
            {
                segmenter.AddPacket(packet);
            }

            segmenter.Complete();

            var keyframes = capture.Packets.Where(p => p.TrackId == VideoTrackId && p.IsKeyframe).ToList();
            var segments = sink.MediaSegments.OrderBy(s => s.Key).Select(s => ParseFmp4(s.Value)).ToList();
            segments.Should().HaveCount(3);
            segments.Select(s => s.SequenceNumber).Should().Equal(1u, 2u, 3u);
            segments.Select(s => s.Trafs[0].BaseMediaDecodeTime).Should().Equal(keyframes[0].Dts, keyframes[2].Dts, keyframes[4].Dts);
            segments.Should().OnlyContain(s => s.Trafs[0].Mp4TrackId == 1 && s.Trafs[0].FirstSampleFlags == 0x02000000u);
            for (var i = 0; i + 1 < segments.Count; i++)
            {
                (segments[i].Trafs[0].BaseMediaDecodeTime + segments[i].Trafs[0].Durations.Sum(d => (long)d)).Should().Be(segments[i + 1].Trafs[0].BaseMediaDecodeTime);
            }

            segments.Sum(s => s.Trafs[0].SampleCount).Should().Be(capture.Packets.Count(p => p.TrackId == VideoTrackId));
            segments.Sum(s => s.Trafs[1].SampleCount).Should().Be(capture.Packets.Count(p => p.TrackId == AudioTrackId));
            segmenter.DroppedLeadingPacketCount.Should().Be(0);

            var lastVideoPts = capture.Packets.Where(p => p.TrackId == VideoTrackId).Max(p => p.Pts);
            PlaylistDurations(sink).Should().Equal(2.0, 2.0, (lastVideoPts + 40 - keyframes[4].Pts) / 1000.0);
            PlaylistText(sink).Should().Contain("#EXT-X-TARGETDURATION:2\n").And.Contain("#EXT-X-MAP:URI=\"init.mp4\"\n");
        }

        [Fact]
        public void Fixture_MpegTs_Should_StartEverySegmentWithARandomAccessKeyframe()
        {
            var capture = FiveSecondCapture();
            var sink = new InMemoryStreamingSink();
            var segmenter = new HlsSegmenter(capture.Demuxer.Tracks!, 2, sink, SegmentFormat.MpegTs);

            foreach (var packet in capture.Packets)
            {
                segmenter.AddPacket(packet);
            }

            segmenter.Complete();

            var keyframes = capture.Packets.Where(p => p.TrackId == VideoTrackId && p.IsKeyframe).ToList();
            var videoPes = new List<List<MpegTsTestReader.Pes>>();
            var audioPesCount = 0;
            foreach (var segment in sink.MediaSegments.OrderBy(s => s.Key).Select(s => s.Value))
            {
                var tsPackets = MpegTsTestReader.ReadPackets(segment);
                MpegTsTestReader.ReadPmt(tsPackets[1]).Streams.Should().Equal(((byte)0x1B, VideoPid), ((byte)0x0F, AudioPid));
                videoPes.Add(MpegTsTestReader.ReadPes(tsPackets, VideoPid));
                audioPesCount += MpegTsTestReader.ReadPes(tsPackets, AudioPid).Count;
            }

            videoPes.Should().HaveCount(3);
            videoPes.Select(p => p[0].First.RandomAccess).Should().AllBeEquivalentTo(true);
            videoPes.Select(p => p[0].DecodeTime).Should().Equal(keyframes[0].Dts * 90, keyframes[2].Dts * 90, keyframes[4].Dts * 90);
            videoPes.Sum(p => p.Count).Should().Be(capture.Packets.Count(p => p.TrackId == VideoTrackId));
            audioPesCount.Should().Be(capture.Packets.Count(p => p.TrackId == AudioTrackId));
            PlaylistText(sink).Should().Contain("segment_1.ts\n").And.NotContain("#EXT-X-MAP");
        }

        [Fact]
        public void AddPacket_SegmentTheMuxerRejects_Should_Throw_DiscardIt_And_ContinueFromTheCutPoint()
        {
            var sink = new InMemoryStreamingSink();
            var segmenter = new HlsSegmenter(Tracks(), 1, sink, SegmentFormat.MpegTs);
            segmenter.AddPacket(Video(0, key: true));
            segmenter.AddPacket(Video(40) with { Payload = new byte[] { 0, 0, 0, 9, 0x41 } });

            var act = () => segmenter.AddPacket(Video(1000, key: true));

            act.Should().ThrowExactly<ArgumentException>();
            sink.MediaSegments.Should().BeEmpty();
            sink.Playlist.Should().BeNull();

            segmenter.AddPacket(Video(2000, key: true));
            segmenter.Complete();

            sink.MediaSegments.Keys.Should().Equal(1L, 2L);
            PlaylistDurations(sink).Should().Equal(1.0, 1.0);
            MpegTsTestReader.ReadPes(MpegTsTestReader.ReadPackets(sink.MediaSegments[1]), VideoPid).Single().DecodeTime.Should().Be(1000 * 90);
        }

        [Fact]
        public void Complete_FinalSegmentTheMuxerRejects_Should_StillEndThePlaylist_And_Throw()
        {
            var sink = new InMemoryStreamingSink();
            var segmenter = new HlsSegmenter(Tracks(), 2, sink);
            segmenter.AddPacket(Video(0, key: true, pts: 3_000_000_000));

            var act = () => segmenter.Complete();

            act.Should().ThrowExactly<ArgumentException>();
            sink.MediaSegments.Should().BeEmpty();
            PlaylistText(sink).Should().EndWith("#EXT-X-ENDLIST\n");
            segmenter.Invoking(s => s.Complete()).Should().NotThrow();
        }

        [Fact]
        public void Ffmpeg_OnPacketDrivenPlaylists_Should_Report_And_Decode_Both_Streams()
        {
            if (Environment.GetEnvironmentVariable("EGGENCODER_FFMPEG_ORACLE") != "1")
            {
                return;
            }

            var capture = FiveSecondCapture();
            foreach (var format in new[] { SegmentFormat.Fmp4, SegmentFormat.MpegTs })
            {
                var sink = new InMemoryStreamingSink();
                var segmenter = new HlsSegmenter(capture.Demuxer.Tracks!, 2, sink, format);
                foreach (var packet in capture.Packets)
                {
                    segmenter.AddPacket(packet);
                }

                segmenter.Complete();

                var directory = Directory.CreateTempSubdirectory("eggencoder-hls-");
                try
                {
                    var playlistPath = WriteToDirectory(sink, directory.FullName, format);

                    var (probeExit, probeOutput, _) = Run("ffprobe", $"-v error -show_entries stream=codec_name,width,height,sample_rate,channels -show_entries format=duration -of compact=p=0 \"{playlistPath}\"");
                    probeExit.Should().Be(0);
                    probeOutput.Should().Contain("codec_name=h264|width=64|height=64");
                    probeOutput.Should().Contain("codec_name=aac|sample_rate=44100|channels=1");
                    var duration = double.Parse(probeOutput.Split('\n').Single(l => l.StartsWith("duration=")).Split('=')[1], CultureInfo.InvariantCulture);
                    duration.Should().BeApproximately(5.0, 0.15);

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

        internal static FlvDemuxerTest.Capture FiveSecondCapture() =>
            FlvDemuxerTest.Demux(File.ReadAllBytes(FiveSecondFixture));

        internal static string WriteToDirectory(InMemoryStreamingSink sink, string directory, SegmentFormat format)
        {
            if (sink.InitSegment is not null)
            {
                File.WriteAllBytes(Path.Combine(directory, HlsSegmenter.InitSegmentUri), sink.InitSegment);
            }

            var extension = format == SegmentFormat.Fmp4 ? "m4s" : "ts";
            foreach (var (sequenceNumber, bytes) in sink.MediaSegments)
            {
                File.WriteAllBytes(Path.Combine(directory, $"segment_{sequenceNumber}.{extension}"), bytes);
            }

            var playlistPath = Path.Combine(directory, "playlist.m3u8");
            File.WriteAllBytes(playlistPath, sink.Playlist!);
            return playlistPath;
        }

        internal static (int ExitCode, string Output, string Errors) Run(string fileName, string arguments)
        {
            var startInfo = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var process = Process.Start(startInfo)!;
            var errors = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output, errors.Result.Trim());
        }

        internal static string PlaylistText(InMemoryStreamingSink sink) => Encoding.UTF8.GetString(sink.Playlist!);

        internal static List<double> PlaylistDurations(InMemoryStreamingSink sink) =>
            PlaylistText(sink).Split('\n')
                .Where(l => l.StartsWith("#EXTINF:"))
                .Select(l => Math.Round(double.Parse(l["#EXTINF:".Length..^1], CultureInfo.InvariantCulture), 6))
                .ToList();

        internal static ParsedSegment ParseFmp4(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes);
            var moof = MovAtomReader.EnumerateAtoms(stream, 0, stream.Length).Single(a => a.Type == "moof");
            var mfhd = MovAtomReader.FindAtom(stream, "mfhd", moof.ContentStart, moof.ContentEnd)!.Value;
            var trafs = new List<ParsedTraf>();
            foreach (var traf in MovAtomReader.EnumerateAtoms(stream, moof.ContentStart, moof.ContentEnd).Where(a => a.Type == "traf"))
            {
                var tfhd = MovAtomReader.FindAtom(stream, "tfhd", traf.ContentStart, traf.ContentEnd)!.Value;
                var tfdt = MovAtomReader.FindAtom(stream, "tfdt", traf.ContentStart, traf.ContentEnd)!.Value;
                var trun = MovAtomReader.FindAtom(stream, "trun", traf.ContentStart, traf.ContentEnd)!.Value;
                var flags = ReadUInt32(bytes, trun.ContentStart) & 0xFFFFFF;
                var count = (int)ReadUInt32(bytes, trun.ContentStart + 4);
                var entrySize = 8 + ((flags & 0x400) != 0 ? 4 : 0) + ((flags & 0x800) != 0 ? 4 : 0);
                var first = trun.ContentStart + 12;
                var durations = Enumerable.Range(0, count).Select(i => ReadUInt32(bytes, first + (i * entrySize))).ToList();
                uint? firstSampleFlags = (flags & 0x400) != 0 ? ReadUInt32(bytes, first + 8) : null;
                trafs.Add(new ParsedTraf(
                    ReadUInt32(bytes, tfhd.ContentStart + 4),
                    (long)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan((int)tfdt.ContentStart + 4)),
                    count,
                    firstSampleFlags,
                    durations));
            }

            return new ParsedSegment(ReadUInt32(bytes, mfhd.ContentStart + 4), trafs);
        }

        private static uint ReadUInt32(byte[] bytes, long position) => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan((int)position));

        private static List<int> SegmentPacketCounts(InMemoryStreamingSink sink) =>
            sink.MediaSegments.OrderBy(s => s.Key).Select(s => ParseFmp4(s.Value).Trafs.Sum(t => t.SampleCount)).ToList();

        private static IEnumerable<MediaPacket> FixturePackets() => FiveSecondCapture().Packets;

        private static MediaTrackSet Tracks() => new([VideoTrack(), AudioTrack()]);

        private static MediaTrackInfo VideoTrack() =>
            new(VideoTrackId, MediaKind.Video, MediaCodec.H264, 1000, FlvTestBuilder.AvcC, 0, width: 64, height: 64);

        private static MediaTrackInfo AudioTrack() =>
            new(AudioTrackId, MediaKind.Audio, MediaCodec.Aac, 1000, FlvTestBuilder.AacConfig, 0, 44100, 1);

        private static MediaPacket Video(long dts, bool key = false, long? pts = null) =>
            new(VideoTrackId, pts ?? dts, dts, key, 0, new byte[] { 0, 0, 0, 1, (byte)(key ? 0x65 : 0x41) });

        private static MediaPacket Audio(long dts) => new(AudioTrackId, dts, dts, true, 0, new byte[] { 0x21, 0x10 });

        internal sealed record ParsedSegment(uint SequenceNumber, List<ParsedTraf> Trafs);

        internal sealed record ParsedTraf(uint Mp4TrackId, long BaseMediaDecodeTime, int SampleCount, uint? FirstSampleFlags, List<uint> Durations);

        private sealed class RecordingSink : IStreamingSink
        {
            public List<long> Written { get; } = [];

            public List<long> Deleted { get; } = [];

            public byte[]? Playlist { get; private set; }

            public void WriteInitSegment(ReadOnlySpan<byte> bytes)
            {
            }

            public void WriteMediaSegment(long sequenceNumber, ReadOnlySpan<byte> bytes) => Written.Add(sequenceNumber);

            public void WritePlaylist(ReadOnlySpan<byte> utf8Bytes) => Playlist = utf8Bytes.ToArray();

            public void DeleteMediaSegment(long sequenceNumber) => Deleted.Add(sequenceNumber);
        }
    }
}
