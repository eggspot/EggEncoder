using EggEncoder.Containers.Flv;
using EggEncoder.Media;
using FluentAssertions;

namespace EggEncoder.UnitTests.Containers.Flv
{
    public class FlvDemuxerTest
    {
        // Ground truth for the fixtures was read off `ffprobe -show_entries packet=stream_index,pts,dts,flags,size`
        // and `ffprobe -show_streams -show_data`. h264_aac.flv: ffmpeg 9.0, libx264 64x64 25 fps with
        // 2 B-frames and a 5-frame GOP + native AAC-LC 44.1 kHz mono, 0.4 s. hevc.flv: libx265 64x64,
        // Enhanced FLV, 5 frames. mp3.flv: libmp3lame 44.1 kHz mono, 9 frames.
        internal static readonly string H264AacFixture = Path.GetFullPath("Containers/Flv/h264_aac.flv");
        internal static readonly string HevcFixture = Path.GetFullPath("Containers/Flv/hevc.flv");
        internal static readonly string Mp3Fixture = Path.GetFullPath("Containers/Flv/mp3.flv");

        private static readonly (long Pts, long Dts, bool Key, int Size)[] ExpectedVideo =
        [
            (80, 0, true, 1998), (200, 40, false, 990), (120, 80, false, 223), (160, 120, false, 43), (240, 160, false, 338),
            (280, 200, true, 2140), (400, 240, false, 618), (320, 280, false, 118), (360, 320, false, 32), (440, 360, false, 297)
        ];

        private static readonly long[] ExpectedAudioPts =
            [57, 80, 103, 126, 150, 173, 196, 219, 243, 266, 289, 312, 335, 359, 382, 405, 428, 452, 475];

        internal sealed class Capture
        {
            public List<MediaTrackSet> TrackSets { get; } = [];
            public List<MediaPacket> Packets { get; } = [];
            public List<int> TrackSetCountAtPacket { get; } = [];
            public FlvDemuxer Demuxer { get; }

            public Capture()
            {
                Demuxer = new FlvDemuxer(TrackSets.Add, packet =>
                {
                    TrackSetCountAtPacket.Add(TrackSets.Count);
                    Packets.Add(packet);
                });
            }

            public Capture FeedAll(byte[] bytes, int chunkSize = int.MaxValue)
            {
                for (var offset = 0; offset < bytes.Length; offset += chunkSize)
                {
                    Demuxer.Feed(bytes.AsSpan(offset, Math.Min(chunkSize, bytes.Length - offset)));
                }

                Demuxer.Complete();
                return this;
            }
        }

        internal static Capture Demux(byte[] bytes, int chunkSize = int.MaxValue) => new Capture().FeedAll(bytes, chunkSize);

        [Fact]
        public void Demux_H264AacFixture_Should_Match_Ffprobe_Packets()
        {
            var capture = Demux(File.ReadAllBytes(H264AacFixture));

            capture.TrackSets.Should().ContainSingle();
            var video = capture.Packets.Where(p => p.TrackId == FlvDemuxer.VideoTrackId).ToList();
            var audio = capture.Packets.Where(p => p.TrackId == FlvDemuxer.AudioTrackId).ToList();
            video.Select(p => (p.Pts, p.Dts, p.IsKeyframe, p.Payload.Length)).Should().Equal(ExpectedVideo);
            audio.Select(p => p.Pts).Should().Equal(ExpectedAudioPts);
            audio.Should().OnlyContain(p => p.Dts == p.Pts && p.IsKeyframe && p.ConfigVersion == 0);
            audio[0].Payload.Length.Should().Be(278);
            capture.Demuxer.SkippedTagCount.Should().Be(0);
        }

        [Fact]
        public void Demux_H264AacFixture_Should_Report_Tracks_And_Codec_Config()
        {
            var capture = Demux(File.ReadAllBytes(H264AacFixture));

            var tracks = capture.Demuxer.Tracks!;
            var video = tracks.FindTrack(FlvDemuxer.VideoTrackId)!;
            var audio = tracks.FindTrack(FlvDemuxer.AudioTrackId)!;
            video.Kind.Should().Be(MediaKind.Video);
            video.Codec.Should().Be(MediaCodec.H264);
            video.Timescale.Should().Be(1000);
            video.Width.Should().Be(64);
            video.Height.Should().Be(64);
            video.CodecConfiguration.ToArray().Should().Equal(FlvTestBuilder.AvcC);
            audio.Codec.Should().Be(MediaCodec.Aac);
            audio.SampleRate.Should().Be(44100);
            audio.Channels.Should().Be(1);
            audio.CodecConfiguration.ToArray().Should().Equal(FlvTestBuilder.AacConfig);
            capture.Demuxer.HeaderDeclaresAudio.Should().BeTrue();
            capture.Demuxer.HeaderDeclaresVideo.Should().BeTrue();
        }

        [Fact]
        public void Demux_H264AacFixture_Should_Read_OnMetaData()
        {
            var metadata = Demux(File.ReadAllBytes(H264AacFixture)).Demuxer.Metadata!;

            metadata.Width.Should().Be(64);
            metadata.Height.Should().Be(64);
            metadata.FrameRate.Should().Be(25);
            metadata.AudioSampleRate.Should().Be(44100);
            metadata.Duration.Should().BeApproximately(0.4, 0.1);
            metadata.Encoder.Should().StartWith("Lavf");
            metadata.VideoDataRate.Should().NotBeNull();
            metadata.AudioDataRate.Should().NotBeNull();
        }

        [Theory]
        [InlineData(1)]
        [InlineData(7)]
        [InlineData(1000)]
        public void Demux_ChunkedFeed_Should_Match_Single_Feed(int chunkSize)
        {
            var bytes = File.ReadAllBytes(H264AacFixture);

            var whole = Demux(bytes);
            var chunked = Demux(bytes, chunkSize);

            chunked.Packets.Select(Describe).Should().Equal(whole.Packets.Select(Describe));
            chunked.TrackSets.Should().HaveCount(1);
        }

        [Fact]
        public void Demux_HevcEnhancedFixture_Should_Match_Ffprobe()
        {
            var capture = Demux(File.ReadAllBytes(HevcFixture));

            var track = capture.Demuxer.Tracks!.Tracks.Should().ContainSingle().Subject;
            track.Codec.Should().Be(MediaCodec.H265);
            track.Width.Should().Be(64);
            track.Height.Should().Be(64);
            track.CodecConfiguration.Length.Should().Be(2437);
            HevcDecoderConfigurationRecord.Parse(track.CodecConfiguration.Span).Should().NotBeNull();
            capture.Packets.Select(p => (p.Pts, p.Dts, p.IsKeyframe, p.Payload.Length)).Should().Equal(
                (80L, 0L, true, 1141), (240L, 40L, false, 43), (160L, 80L, false, 14), (120L, 120L, false, 15), (200L, 160L, false, 14));
            capture.Demuxer.HeaderDeclaresAudio.Should().BeFalse();
            capture.Demuxer.SkippedTagCount.Should().Be(0, "the colorInfo metadata packet is ignored, not skipped as unusable");
        }

        [Fact]
        public void Demux_Mp3Fixture_Should_Create_Track_From_First_Frame()
        {
            var capture = Demux(File.ReadAllBytes(Mp3Fixture));

            var track = capture.Demuxer.Tracks!.Tracks.Should().ContainSingle().Subject;
            track.Codec.Should().Be(MediaCodec.Mp3);
            track.SampleRate.Should().Be(44100);
            track.Channels.Should().Be(1);
            track.CodecConfiguration.IsEmpty.Should().BeTrue();
            capture.Packets.Select(p => p.Pts).Should().Equal(0, 26, 52, 78, 104, 131, 157, 183, 209);
            capture.Packets[0].Payload.Length.Should().Be(208);
        }

        [Fact]
        public void Demux_Mp3With48kHzFrame_Should_Use_Mpeg_Header_Rate_And_Stereo()
        {
            // MPEG-1 Layer III, 48 kHz (rate index 1), joint stereo; FLV's own rate field can only say 44 kHz.
            var bytes = new FlvTestBuilder(flags: 0x04).Audio(0, 0x2F, 0xFF, 0xFB, 0x94, 0x40).ToArray();

            var track = Demux(bytes).Demuxer.Tracks!.Tracks.Single();

            track.SampleRate.Should().Be(48000);
            track.Channels.Should().Be(2);
        }

        [Theory]
        [InlineData(0xF3, 0x80, 22050)] // MPEG-2, 22.05 kHz
        [InlineData(0xE3, 0x80, 11025)] // MPEG-2.5, 11.025 kHz
        [InlineData(0xFB, 0x88, 32000)] // MPEG-1, 32 kHz
        public void Demux_Mp3MpegVersions_Should_Derive_Sample_Rate(byte b1, byte b2, int rate)
        {
            var bytes = new FlvTestBuilder(flags: 0x04).Audio(0, 0x22, 0xFF, b1, b2, 0xC0).ToArray();

            var track = Demux(bytes).Demuxer.Tracks!.Tracks.Single();

            track.SampleRate.Should().Be(rate);
            track.Channels.Should().Be(1);
        }

        [Theory]
        [InlineData(new byte[] { 0x2B, 0x00, 0x00, 0x00, 0x00 })] // no sync word
        [InlineData(new byte[] { 0x2B, 0xFF, 0xEB, 0x90, 0x00 })] // reserved MPEG version
        [InlineData(new byte[] { 0x2B, 0xFF, 0xFB, 0x9C, 0x00 })] // reserved rate index
        [InlineData(new byte[] { 0x2B, 0xFF })] // too short
        public void Demux_Mp3UnreadableHeader_Should_Fall_Back_To_Flv_Hints(byte[] body)
        {
            var bytes = new FlvTestBuilder(flags: 0x04).Audio(0, body).ToArray();

            var track = Demux(bytes).Demuxer.Tracks!.Tracks.Single();

            track.SampleRate.Should().Be(22050);
            track.Channels.Should().Be(2);
        }

        [Fact]
        public void Demux_PacketsBeforeAllDeclaredTracks_Should_Be_Held_Until_Both_Are_Configured()
        {
            var bytes = new FlvTestBuilder()
                .AvcSequenceHeader()
                .AvcFrame(0, true, 0, 0, 0, 0, 1, 0x65)
                .AacSequenceHeader(0)
                .AacFrame(0, 0x21)
                .ToArray();

            var capture = Demux(bytes);

            capture.TrackSets.Should().ContainSingle().Which.Tracks.Should().HaveCount(2);
            capture.TrackSetCountAtPacket.Should().OnlyContain(count => count == 1);
            capture.Packets.Select(p => p.TrackId).Should().Equal(FlvDemuxer.VideoTrackId, FlvDemuxer.AudioTrackId);
        }

        [Fact]
        public void Demux_DeclaredTrackThatNeverAppears_Should_Announce_On_Complete()
        {
            var bytes = new FlvTestBuilder().AvcSequenceHeader().AvcFrame(0, true, 0, 0, 0, 0, 1, 0x65).ToArray();
            var capture = new Capture();

            capture.Demuxer.Feed(bytes);
            capture.Packets.Should().BeEmpty();
            capture.Demuxer.Tracks.Should().BeNull();
            capture.Demuxer.Complete();

            capture.TrackSets.Should().ContainSingle().Which.Tracks.Should().ContainSingle().Which.Kind.Should().Be(MediaKind.Video);
            capture.Packets.Should().ContainSingle();
        }

        [Fact]
        public void Demux_PendingLimitReached_Should_Announce_Known_Tracks_Then_Add_Late_Track()
        {
            var builder = new FlvTestBuilder().AvcSequenceHeader();
            for (var i = 0; i < FlvDemuxer.MaxPendingPackets; i++)
            {
                builder.AvcFrame((uint)(i * 40), i == 0, 0, 0, 0, 0, 1, 0x41);
            }

            builder.AacSequenceHeader((uint)(FlvDemuxer.MaxPendingPackets * 40)).AacFrame((uint)(FlvDemuxer.MaxPendingPackets * 40), 0x21);

            var capture = Demux(builder.ToArray());

            capture.TrackSets.Should().HaveCount(2);
            capture.TrackSets[0].Tracks.Should().ContainSingle();
            capture.TrackSets[1].Tracks.Should().HaveCount(2);
            capture.Packets.Should().HaveCount(FlvDemuxer.MaxPendingPackets + 1);
            capture.TrackSetCountAtPacket[^1].Should().Be(2);
        }

        [Fact]
        public void Demux_HeaderWithoutFlags_Should_Announce_On_First_Configured_Track()
        {
            var bytes = new FlvTestBuilder(flags: 0).AacSequenceHeader().AacFrame(0, 0x21).ToArray();

            var capture = Demux(bytes);

            capture.TrackSets.Should().ContainSingle();
            capture.Packets.Should().ContainSingle();
        }

        [Fact]
        public void Demux_NewSequenceHeader_Should_Bump_ConfigVersion_And_Republish_Tracks()
        {
            var otherConfig = Convert.FromHexString("1210");
            var bytes = new FlvTestBuilder(flags: 0x04)
                .AacSequenceHeader()
                .AacFrame(0, 1)
                .AacSequenceHeader(23) // identical bytes: no change
                .AacFrame(23, 2)
                .AacSequenceHeader(46, otherConfig)
                .AacFrame(46, 3)
                .ToArray();

            var capture = Demux(bytes);

            capture.TrackSets.Should().HaveCount(2);
            capture.TrackSets[1].Tracks.Single().ConfigVersion.Should().Be(1);
            capture.TrackSets[1].Tracks.Single().CodecConfiguration.ToArray().Should().Equal(otherConfig);
            capture.TrackSets[1].Tracks.Single().Channels.Should().Be(2);
            capture.Packets.Select(p => p.ConfigVersion).Should().Equal(0, 0, 1);
        }

        [Fact]
        public void Demux_CodecChangeOnVideoTrack_Should_Bump_ConfigVersion()
        {
            var hvcC = HevcConfig();
            var bytes = new FlvTestBuilder(flags: 0x01)
                .AvcSequenceHeader()
                .AvcFrame(0, true, 0, 0, 0, 0, 1, 0x65)
                .Video(40, [0x90, (byte)'h', (byte)'v', (byte)'c', (byte)'1', .. hvcC])
                .Video(40, [0x93, (byte)'h', (byte)'v', (byte)'c', (byte)'1', 0, 0, 0, 1, 0x26])
                .ToArray();

            var capture = Demux(bytes);

            capture.TrackSets[^1].Tracks.Single().Codec.Should().Be(MediaCodec.H265);
            capture.TrackSets[^1].Tracks.Single().ConfigVersion.Should().Be(1);
            capture.Packets.Select(p => p.ConfigVersion).Should().Equal(0, 1);
        }

        [Fact]
        public void Demux_FrameBeforeSequenceHeader_Should_Be_Skipped()
        {
            var bytes = new FlvTestBuilder(flags: 0x01)
                .AvcFrame(0, true, 0, 0, 0, 0, 1, 0x65)
                .AvcSequenceHeader(40)
                .AvcFrame(40, true, 0, 0, 0, 0, 1, 0x65)
                .ToArray();

            var capture = Demux(bytes);

            capture.Packets.Should().ContainSingle().Which.Dts.Should().Be(40);
            capture.Demuxer.SkippedTagCount.Should().Be(1);
        }

        [Fact]
        public void Demux_FrameWithCodecDifferentFromTrack_Should_Be_Skipped()
        {
            var bytes = new FlvTestBuilder(flags: 0x05)
                .AacSequenceHeader()
                .Audio(0, 0x2E, 0xFF, 0xFB, 0x90, 0xC0) // an MP3 frame on an AAC track
                .ToArray();

            var capture = Demux(bytes);

            capture.Packets.Should().BeEmpty();
            capture.Demuxer.SkippedTagCount.Should().Be(1);
        }

        [Fact]
        public void Demux_UnusableTags_Should_Be_Counted_Not_Thrown()
        {
            var bytes = new FlvTestBuilder(flags: 0x04)
                .AacSequenceHeader()
                .Audio(0, 0x00, 1, 2) // linear PCM: unsupported codec
                .Tag(0x28, 0, 0xAF, 1, 1) // filter bit set: encrypted
                .Tag(0x0F, 0, 1, 2, 3) // unknown tag type
                .Tag(18, 0, 0x02, 0, 0x0A, (byte)'o', (byte)'n', (byte)'C', (byte)'u', (byte)'e', (byte)'P', (byte)'o', (byte)'i', (byte)'n', (byte)'t')
                .Tag(18, 0, 0xFF) // unreadable script data
                .Tag(18, 0, [0x02, 0, 0x0A, .. "onMetaData"u8.ToArray(), 0x05]) // onMetaData not followed by an object
                .Video(0, 0x17, 2, 0, 0, 0) // AVC end of sequence: valid, carries nothing
                .AacFrame(0, 0x21)
                .ToArray();

            var capture = Demux(bytes);

            capture.Packets.Should().ContainSingle();
            capture.Demuxer.SkippedTagCount.Should().Be(6);
            capture.Demuxer.Metadata.Should().BeNull();
        }

        [Fact]
        public void Demux_SetDataFrameWrapper_Should_Be_Read_As_Metadata()
        {
            var body = EggEncoder.Live.Rtmp.Amf0.Amf0Writer.Encode(
                EggEncoder.Live.Rtmp.Amf0.Amf0Value.FromString("@setDataFrame"),
                EggEncoder.Live.Rtmp.Amf0.Amf0Value.FromString("onMetaData"),
                EggEncoder.Live.Rtmp.Amf0.Amf0Value.FromObject([new("width", EggEncoder.Live.Rtmp.Amf0.Amf0Value.FromNumber(1920))]));
            var bytes = new FlvTestBuilder(flags: 0).Tag(18, 0, body).ToArray();

            var capture = Demux(bytes);

            capture.Demuxer.Metadata!.Width.Should().Be(1920);
            capture.Demuxer.Metadata.Encoder.Should().BeNull();
            capture.TrackSets.Should().BeEmpty();
        }

        [Fact]
        public void Demux_TimestampWrap_Should_Keep_Counting_Up()
        {
            var bytes = new FlvTestBuilder(flags: 0x04)
                .AacSequenceHeader(0xFFFFFF00)
                .AacFrame(0xFFFFFFF0, 1)
                .AacFrame(0x00000010, 2)
                .AacFrame(0x00000005, 3) // a small step back stays a step back
                .ToArray();

            var capture = Demux(bytes);

            capture.Packets.Select(p => p.Dts).Should().Equal(0xFFFFFFF0L, 0x100000010L, 0x100000005L);
        }

        [Fact]
        public void Demux_ExtendedTimestampByte_Should_Form_The_Upper_Eight_Bits()
        {
            var bytes = new FlvTestBuilder(flags: 0x04).AacSequenceHeader().AacFrame(0x12345678, 1).ToArray();

            Demux(bytes).Packets.Single().Dts.Should().Be(0x12345678);
        }

        [Fact]
        public void Demux_NegativeCompositionTime_Should_Give_Pts_Below_Dts()
        {
            var bytes = new FlvTestBuilder(flags: 0x01).AvcSequenceHeader().AvcFrame(100, false, -40, 0, 0, 0, 1, 0x41).ToArray();

            var packet = Demux(bytes).Packets.Single();

            packet.Dts.Should().Be(100);
            packet.Pts.Should().Be(60);
            packet.CompositionOffset.Should().Be(-40);
        }

        [Fact]
        public void Demux_DataOffsetPadding_Should_Be_Skipped()
        {
            var bytes = new FlvTestBuilder(flags: 0x04, dataOffset: 20).AacSequenceHeader().AacFrame(0, 1).ToArray();

            Demux(bytes, chunkSize: 3).Packets.Should().ContainSingle();
        }

        [Fact]
        public void Demux_MissingFinalPreviousTagSize_Should_Be_Tolerated()
        {
            var bytes = new FlvTestBuilder(flags: 0x04).AacSequenceHeader().AacFrame(0, 1).WithoutTrailingPreviousTagSize().ToArray();

            Demux(bytes).Packets.Should().ContainSingle();
        }

        [Fact]
        public void Demux_HeaderOnly_Should_Complete_With_No_Tracks()
        {
            var capture = Demux(new FlvTestBuilder().ToArray());

            capture.TrackSets.Should().BeEmpty();
            capture.Demuxer.Tracks.Should().BeNull();
        }

        [Fact]
        public void Demux_Av1AndVp9SequenceHeaders_Should_Create_Tracks_Without_Dimensions()
        {
            var av1 = new FlvTestBuilder(flags: 0x01)
                .Video(0, 0x90, (byte)'a', (byte)'v', (byte)'0', (byte)'1', 0x81, 0x00, 0x0C, 0x00)
                .Video(0, 0x91, (byte)'a', (byte)'v', (byte)'0', (byte)'1', 0x12, 0x00)
                .ToArray();
            var vp9 = new FlvTestBuilder(flags: 0x01).Video(0, 0x90, (byte)'v', (byte)'p', (byte)'0', (byte)'9', 1, 2).ToArray();

            var av1Capture = Demux(av1);
            var vp9Track = Demux(vp9).Demuxer.Tracks!.Tracks.Single();

            av1Capture.Demuxer.Tracks!.Tracks.Single().Codec.Should().Be(MediaCodec.Av1);
            av1Capture.Packets.Single().Payload.ToArray().Should().Equal(0x12, 0x00);
            vp9Track.Codec.Should().Be(MediaCodec.Vp9);
            vp9Track.Width.Should().Be(0);
        }

        [Fact]
        public void Demux_UnparseableSps_Should_Leave_Dimensions_Unknown()
        {
            // A structurally valid avcC whose single SPS is just a NAL header byte.
            byte[] avcC = [1, 0x4D, 0x40, 0x0A, 0xFF, 0xE1, 0, 1, 0x67, 1, 0, 1, 0x68];
            var bytes = new FlvTestBuilder(flags: 0x01).AvcSequenceHeader(0, avcC).ToArray();

            var track = Demux(bytes).Demuxer.Tracks!.Tracks.Single();

            track.Width.Should().Be(0);
            track.Height.Should().Be(0);
        }

        [Fact]
        public void Demux_AacConfigWithEscapes_Should_Read_Explicit_Rate()
        {
            // audioObjectType 31 + 6 escape bits, samplingFrequencyIndex 15 + 24-bit rate 7350, 2 channels.
            var bits = "11111" + "000001" + "1111" + Convert.ToString(7350, 2).PadLeft(24, '0') + "0010";
            bits = bits.PadRight((bits.Length + 7) / 8 * 8, '0');
            var config = Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
            var bytes = new FlvTestBuilder(flags: 0x04).AacSequenceHeader(0, config).ToArray();

            var track = Demux(bytes).Demuxer.Tracks!.Tracks.Single();

            track.SampleRate.Should().Be(7350);
            track.Channels.Should().Be(2);
        }

        public static TheoryData<string, byte[]> MalformedStreams => new()
        {
            { "bad signature", [(byte)'F', (byte)'L', (byte)'X', 1, 5, 0, 0, 0, 9, 0, 0, 0, 0] },
            { "bad version", [(byte)'F', (byte)'L', (byte)'V', 2, 5, 0, 0, 0, 9, 0, 0, 0, 0] },
            { "DataOffset below 9", [(byte)'F', (byte)'L', (byte)'V', 1, 5, 0, 0, 0, 8, 0, 0, 0, 0] },
            { "DataOffset huge", [(byte)'F', (byte)'L', (byte)'V', 1, 5, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0] },
            { "PreviousTagSize0 not zero", [(byte)'F', (byte)'L', (byte)'V', 1, 5, 0, 0, 0, 9, 0, 0, 0, 1] },
            { "PreviousTagSize mismatch", PatchLastPreviousTagSize(new FlvTestBuilder().AacSequenceHeader().ToArray()) },
            { "truncated avcC", new FlvTestBuilder(flags: 0x01).AvcSequenceHeader(0, FlvTestBuilder.AvcC[..10]).ToArray() },
            { "avcC with no SPS", new FlvTestBuilder(flags: 0x01).AvcSequenceHeader(0, [1, 0x4D, 0x40, 0x0A, 0xFF, 0xE0, 0]).ToArray() },
            { "hvcC with no SPS", new FlvTestBuilder(flags: 0x01).Video(0, [0x90, (byte)'h', (byte)'v', (byte)'c', (byte)'1', .. HevcConfigWithoutSps()]).ToArray() },
            { "empty AV1 sequence header", new FlvTestBuilder(flags: 0x01).Video(0, 0x90, (byte)'a', (byte)'v', (byte)'0', (byte)'1').ToArray() },
            { "truncated AudioSpecificConfig", new FlvTestBuilder(flags: 0x04).AacSequenceHeader(0, [0x12]).ToArray() },
            { "reserved AAC frequency index", new FlvTestBuilder(flags: 0x04).AacSequenceHeader(0, [0x16, 0x88]).ToArray() },
            { "empty video tag body", new FlvTestBuilder(flags: 0x01).Video(0).ToArray() },
        };

        [Theory]
        [MemberData(nameof(MalformedStreams))]
        public void Demux_MalformedStream_Should_Throw_InvalidDataException(string description, byte[] bytes)
        {
            var act = () => Demux(bytes);

            act.Should().Throw<InvalidDataException>(description);
        }

        [Fact]
        public void Complete_TruncatedHeader_Should_Throw()
        {
            var act = () => Demux([(byte)'F', (byte)'L', (byte)'V']);

            act.Should().Throw<InvalidDataException>().WithMessage("*file header*");
        }

        [Fact]
        public void Complete_TagDataSizePastEnd_Should_Throw()
        {
            var bytes = new FlvTestBuilder().AacSequenceHeader().ToArray();
            bytes[13 + 3] = 0xFF; // DataSize low byte: now claims far more than is there

            var act = () => Demux(bytes[..^4]);

            act.Should().Throw<InvalidDataException>().WithMessage("*incomplete tag*");
        }

        [Fact]
        public void Complete_PartialPreviousTagSize_Should_Throw()
        {
            var bytes = new FlvTestBuilder(flags: 0x04).AacSequenceHeader().ToArray();

            var act = () => Demux(bytes[..^2]);

            act.Should().Throw<InvalidDataException>();
        }

        [Fact]
        public void Feed_AfterComplete_Should_Throw_And_Complete_Twice_Should_Be_A_NoOp()
        {
            var capture = Demux(new FlvTestBuilder().ToArray());

            capture.Demuxer.Complete();
            var act = () => capture.Demuxer.Feed([0]);

            act.Should().Throw<InvalidOperationException>();
        }

        [Fact]
        public void Constructor_NullCallbacks_Should_Throw()
        {
            ((Action)(() => new FlvDemuxer(null!, _ => { }))).Should().Throw<ArgumentNullException>();
            ((Action)(() => new FlvDemuxer(_ => { }, null!))).Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void RandomlyCorruptedFixture_Should_Only_Ever_Throw_InvalidDataException()
        {
            var original = File.ReadAllBytes(H264AacFixture);
            var random = new Random(42);
            for (var i = 0; i < 300; i++)
            {
                var bytes = (byte[])original.Clone();
                for (var j = 0; j < 4; j++)
                {
                    bytes[random.Next(13, bytes.Length)] = (byte)random.Next(256);
                }

                try
                {
                    Demux(bytes[..random.Next(13, bytes.Length + 1)], chunkSize: random.Next(1, 5000));
                }
                catch (InvalidDataException)
                {
                }
            }
        }

        private static string Describe(MediaPacket packet) =>
            $"{packet.TrackId}:{packet.Pts}:{packet.Dts}:{packet.IsKeyframe}:{packet.ConfigVersion}:{Convert.ToHexString(packet.Payload.Span)}";

        private static byte[] PatchLastPreviousTagSize(byte[] bytes)
        {
            bytes[^1] ^= 0x01;
            return bytes;
        }

        internal static byte[] HevcConfig()
        {
            var bytes = File.ReadAllBytes(HevcFixture);
            // First tag after the onMetaData tag (183-byte body) is the HEVC SequenceStart.
            var offset = 13 + 11 + 183 + 4;
            var size = bytes[offset + 1] << 16 | bytes[offset + 2] << 8 | bytes[offset + 3];
            return bytes[(offset + 11 + 5)..(offset + 11 + size)];
        }

        private static byte[] HevcConfigWithoutSps()
        {
            var record = HevcDecoderConfigurationRecord.Parse(HevcConfig());
            var arrays = record.Arrays.Where(a => a.NalUnitType != HevcDecoderConfigurationRecord.SpsNalUnitType).ToList();
            return (record with { Arrays = arrays }).ToArray();
        }
    }
}
