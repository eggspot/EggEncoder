using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using EggEncoder.Codecs.Mov;
using EggEncoder.Containers.Flv;
using EggEncoder.Media;
using EggEncoder.Streaming.Mp4;
using EggEncoder.UnitTests.Containers.Flv;
using EggEncoder.UnitTests.Media;
using FluentAssertions;

namespace EggEncoder.UnitTests.Streaming.Mp4
{
    // The packet-model (MediaTrackSet/MediaPacket) half of FmpInitSegmentWriter and
    // FmpMediaSegmentWriter: docs/live-ingest-restream-plan.md item 5a. The frame-mode half stays in
    // FmpSegmentWriterTest, unchanged.
    public class FmpPacketSegmentWriterTest
    {
        private const uint SyncFlags = 0x02000000;
        private const uint NonSyncFlags = 0x01010000;

        // ---- Init segment ----

        [Fact]
        public void BuildInitSegment_H264AacTrackSet_Should_Describe_Both_Tracks()
        {
            var tracks = H264AacCapture().Demuxer.Tracks!;

            var init = FmpInitSegmentWriter.BuildInitSegment(tracks);

            using var stream = new MemoryStream(init);
            var moov = MovAtomReader.EnumerateAtoms(stream, 0, stream.Length).Single(a => a.Type == "moov");
            var mvhd = MovAtomReader.FindAtom(stream, "mvhd", moov.ContentStart, moov.ContentEnd)!.Value;
            ReadUInt32(stream, mvhd.ContentStart + 12).Should().Be(1000, "the packet path's movie timescale");
            ReadUInt32(stream, mvhd.ContentEnd - 4).Should().Be(3, "next_track_ID is the track count plus one");

            var traks = MovAtomReader.EnumerateAtoms(stream, moov.ContentStart, moov.ContentEnd).Where(a => a.Type == "trak").ToList();
            traks.Should().HaveCount(2);

            var video = ReadTrak(stream, traks[0]);
            video.TrackId.Should().Be(1);
            video.Handler.Should().Be("vide");
            video.Timescale.Should().Be(1000);
            video.TkhdVolume.Should().Be(0);
            video.TkhdWidth.Should().Be(64u << 16);
            video.TkhdHeight.Should().Be(64u << 16);
            video.MediaHeader.Should().Be("vmhd");
            video.SampleEntry.Should().Be("avc1");
            ReadUInt16(stream, video.SampleEntryContentStart + 24).Should().Be(64, "VisualSampleEntry width");
            ReadUInt16(stream, video.SampleEntryContentStart + 26).Should().Be(64, "VisualSampleEntry height");
            ReadUInt16(stream, video.SampleEntryContentStart + 74).Should().Be(0x0018, "VisualSampleEntry depth");
            ReadChildBox(stream, video.SampleEntryContentStart + 78, video.SampleEntryContentEnd, "avcC").Should().Equal(FlvTestBuilder.AvcC);

            var audio = ReadTrak(stream, traks[1]);
            audio.TrackId.Should().Be(2);
            audio.Handler.Should().Be("soun");
            audio.Timescale.Should().Be(1000, "each track keeps its own timescale");
            audio.TkhdVolume.Should().Be(0x0100);
            audio.TkhdWidth.Should().Be(0);
            audio.MediaHeader.Should().Be("smhd");
            audio.SampleEntry.Should().Be("mp4a");
            ReadUInt16(stream, audio.SampleEntryContentStart + 16).Should().Be(1, "mp4a channelcount");
            ReadUInt32(stream, audio.SampleEntryContentStart + 24).Should().Be(44100u << 16, "mp4a samplerate, 16.16 fixed");
            var esds = ReadChildBox(stream, audio.SampleEntryContentStart + 28, audio.SampleEntryContentEnd, "esds");
            esds.AsSpan().IndexOf(FlvTestBuilder.AacConfig).Should().BePositive("the AudioSpecificConfig is copied as-is, not rebuilt");

            var mvex = MovAtomReader.FindAtom(stream, "mvex", moov.ContentStart, moov.ContentEnd)!.Value;
            var trexes = MovAtomReader.EnumerateAtoms(stream, mvex.ContentStart, mvex.ContentEnd).Where(a => a.Type == "trex").ToList();
            trexes.Select(t => ReadUInt32(stream, t.ContentStart + 4)).Should().Equal(1u, 2u);

            MovAtomReader.FindTrackByHandlerType(stream, moov, "vide").Should().Be(traks[0]);
            MovAtomReader.FindTrackByHandlerType(stream, moov, "soun").Should().Be(traks[1]);
        }

        [Fact]
        public void BuildInitSegment_HevcTrack_Should_Write_Hvc1_With_HvcC()
        {
            var tracks = FlvDemuxerTest.Demux(File.ReadAllBytes(FlvDemuxerTest.HevcFixture)).Demuxer.Tracks!;
            var hevc = tracks.Tracks.Single(t => t.Codec == MediaCodec.H265);

            var init = FmpInitSegmentWriter.BuildInitSegment(tracks);

            using var stream = new MemoryStream(init);
            var moov = MovAtomReader.EnumerateAtoms(stream, 0, stream.Length).Single(a => a.Type == "moov");
            var trak = MovAtomReader.EnumerateAtoms(stream, moov.ContentStart, moov.ContentEnd).First(a => a.Type == "trak");
            var video = ReadTrak(stream, trak);
            video.SampleEntry.Should().Be("hvc1");
            video.TkhdWidth.Should().Be((uint)hevc.Width << 16);
            ReadChildBox(stream, video.SampleEntryContentStart + 78, video.SampleEntryContentEnd, "hvcC").Should().Equal(hevc.CodecConfiguration.ToArray());
        }

        [Theory]
        [InlineData(MediaCodec.H264)]
        [InlineData(MediaCodec.H265)]
        public void BuildInitSegment_VideoTrackWithUnknownDimensions_Should_Read_Them_From_The_Sps(MediaCodec codec)
        {
            var config = codec == MediaCodec.H264 ? MediaFixtures.Avc1080pRecord : MediaFixtures.Hevc1080pRecord;
            var track = new MediaTrackInfo(5, MediaKind.Video, codec, 90000, config, 0);

            var video = ReadSingleTrak(FmpInitSegmentWriter.BuildInitSegment(new MediaTrackSet([track])));

            video.TkhdWidth.Should().Be(1920u << 16);
            video.TkhdHeight.Should().Be(1080u << 16);
            video.Timescale.Should().Be(90000);
        }

        [Fact]
        public void BuildInitSegment_VideoTrackWithOnlyOneDimension_Should_Take_Both_From_The_Sps()
        {
            var track = new MediaTrackInfo(0, MediaKind.Video, MediaCodec.H264, 1000, MediaFixtures.Avc1080pRecord, 0, width: 640);

            var video = ReadSingleTrak(FmpInitSegmentWriter.BuildInitSegment(new MediaTrackSet([track])));

            video.TkhdWidth.Should().Be(1920u << 16);
            video.TkhdHeight.Should().Be(1080u << 16);
        }

        [Fact]
        public void BuildInitSegment_AacTrackWithOnlyChannelsUnknown_Should_Keep_Its_Sample_Rate()
        {
            // The config says 44100 Hz mono; the track's own sample rate wins, the channels come from the config.
            var track = new MediaTrackInfo(0, MediaKind.Audio, MediaCodec.Aac, 1000, FlvTestBuilder.AacConfig, 0, sampleRate: 22050);

            using var stream = new MemoryStream(FmpInitSegmentWriter.BuildInitSegment(new MediaTrackSet([track])));
            var audio = ReadSingleTrak(stream);

            ReadUInt16(stream, audio.SampleEntryContentStart + 16).Should().Be(1);
            ReadUInt32(stream, audio.SampleEntryContentStart + 24).Should().Be(22050u << 16);
        }

        [Fact]
        public void BuildInitSegment_VideoTrackWithUnreadableSps_Should_Leave_Dimensions_Zero()
        {
            // A structurally valid avcC whose single SPS is just a NAL header byte.
            byte[] avcC = [1, 0x42, 0, 0x1F, 0xFF, 0xE1, 0, 1, 0x67, 1, 0, 1, 0x68];
            var track = new MediaTrackInfo(0, MediaKind.Video, MediaCodec.H264, 1000, avcC, 0);

            var video = ReadSingleTrak(FmpInitSegmentWriter.BuildInitSegment(new MediaTrackSet([track])));

            video.TkhdWidth.Should().Be(0);
            video.TkhdHeight.Should().Be(0);
        }

        [Fact]
        public void BuildInitSegment_AacTrackWithUnknownParameters_Should_Read_Them_From_The_AudioSpecificConfig()
        {
            // AAC-LC, 48000 Hz (index 3), stereo.
            byte[] config = [0x11, 0x90];
            var track = new MediaTrackInfo(0, MediaKind.Audio, MediaCodec.Aac, 48000, config, 0);

            using var stream = new MemoryStream(FmpInitSegmentWriter.BuildInitSegment(new MediaTrackSet([track])));
            var audio = ReadSingleTrak(stream);

            ReadUInt16(stream, audio.SampleEntryContentStart + 16).Should().Be(2, "stereo is accepted on the packet path");
            ReadUInt32(stream, audio.SampleEntryContentStart + 24).Should().Be(48000u << 16);
        }

        [Fact]
        public void BuildInitSegment_AacTrackAbove65535Hz_Should_Write_Zero_SampleRate()
        {
            // AAC-LC, 96000 Hz (index 0), mono.
            byte[] config = [0x10, 0x08];
            var track = new MediaTrackInfo(0, MediaKind.Audio, MediaCodec.Aac, 96000, config, 0, 96000, 1);

            using var stream = new MemoryStream(FmpInitSegmentWriter.BuildInitSegment(new MediaTrackSet([track])));
            var audio = ReadSingleTrak(stream);

            ReadUInt32(stream, audio.SampleEntryContentStart + 24).Should().Be(0, "96000 doesn't fit the 16.16 field");
            audio.Timescale.Should().Be(96000);
        }

        [Theory]
        [InlineData(2)]
        [InlineData(127)]
        [InlineData(200)]
        [InlineData(20_000)]
        public void BuildInitSegment_LongAudioSpecificConfig_Should_Get_Multibyte_Descriptor_Lengths(int length)
        {
            // AAC-LC 44100 mono, then padding standing in for a long program config element.
            var config = new byte[length];
            config[0] = 0x12;
            config[1] = 0x08;
            config.AsSpan(2).Fill(0x5A);
            var track = new MediaTrackInfo(0, MediaKind.Audio, MediaCodec.Aac, 44100, config, 0, 44100, 1);

            using var stream = new MemoryStream(FmpInitSegmentWriter.BuildInitSegment(new MediaTrackSet([track])));
            var audio = ReadSingleTrak(stream);
            var esds = ReadChildBox(stream, audio.SampleEntryContentStart + 28, audio.SampleEntryContentEnd, "esds");

            Mp4EsdsParser.ParseAudioSpecificConfig(esds).SampleRate.Should().Be(44100, "the descriptor walk finds the ASC through every length");
            esds.AsSpan(esds.Length - 3 - length, length).ToArray().Should().Equal(config, "the ASC precedes the 3-byte SLConfigDescriptor");
            // esds: version/flags (4 bytes), ES_Descriptor tag, then its length's first byte.
            (esds[5] >= 0x80).Should().Be(length >= 105, "lengths below 128 keep the single-byte form, longer ones expand");
        }

        [Fact]
        public void BuildInitSegment_Null_Should_Throw()
        {
            var act = () => FmpInitSegmentWriter.BuildInitSegment((MediaTrackSet)null!);

            act.Should().ThrowExactly<ArgumentNullException>();
        }

        [Fact]
        public void BuildInitSegment_EmptyTrackSet_Should_Throw()
        {
            var act = () => FmpInitSegmentWriter.BuildInitSegment(new MediaTrackSet([]));

            act.Should().ThrowExactly<ArgumentException>();
        }

        [Theory]
        [InlineData(MediaCodec.Mp3)]
        [InlineData(MediaCodec.Opus)]
        [InlineData(MediaCodec.Av1)]
        [InlineData(MediaCodec.Vp9)]
        public void BuildInitSegment_UnsupportedCodec_Should_Throw(MediaCodec codec)
        {
            var track = new MediaTrackInfo(0, MediaKind.Audio, codec, 1000, new byte[] { 1, 2, 3 }, 0, 44100, 2);

            var act = () => FmpInitSegmentWriter.BuildInitSegment(new MediaTrackSet([track]));

            act.Should().ThrowExactly<NotSupportedException>();
        }

        public static TheoryData<string, MediaTrackInfo> InvalidTracks => new()
        {
            { "empty AAC config", new MediaTrackInfo(0, MediaKind.Audio, MediaCodec.Aac, 1000, ReadOnlyMemory<byte>.Empty, 0, 44100, 1) },
            { "truncated AAC config", new MediaTrackInfo(0, MediaKind.Audio, MediaCodec.Aac, 1000, new byte[] { 0x12 }, 0) },
            // samplingFrequencyIndex 15 with an explicit 24-bit rate of 0.
            { "AAC rate 0", new MediaTrackInfo(0, MediaKind.Audio, MediaCodec.Aac, 1000, new byte[] { 0x17, 0x80, 0, 0, 0x08 }, 0, 0, 1) },
            { "AAC 65536 channels", new MediaTrackInfo(0, MediaKind.Audio, MediaCodec.Aac, 1000, FlvTestBuilder.AacConfig, 0, 44100, 65536) },
            { "bad avcC", new MediaTrackInfo(0, MediaKind.Video, MediaCodec.H264, 1000, new byte[] { 1, 2 }, 0, width: 64, height: 64) },
            { "bad hvcC", new MediaTrackInfo(0, MediaKind.Video, MediaCodec.H265, 1000, new byte[] { 1, 2 }, 0, width: 64, height: 64) },
            { "width 65536", new MediaTrackInfo(0, MediaKind.Video, MediaCodec.H264, 1000, FlvTestBuilder.AvcC, 0, width: 65536, height: 64) },
            { "height 65536", new MediaTrackInfo(0, MediaKind.Video, MediaCodec.H264, 1000, FlvTestBuilder.AvcC, 0, width: 64, height: 65536) },
        };

        [Theory]
        [MemberData(nameof(InvalidTracks))]
        public void BuildInitSegment_InvalidTrack_Should_Throw(string reason, MediaTrackInfo track)
        {
            var act = () => FmpInitSegmentWriter.BuildInitSegment(new MediaTrackSet([track]));

            act.Should().ThrowExactly<ArgumentException>(reason).Which.ParamName.Should().Be("tracks");
        }

        [Theory]
        [MemberData(nameof(InvalidTracks))]
        public void Constructor_InvalidTrack_Should_Throw_Like_The_Init_Writer(string reason, MediaTrackInfo track)
        {
            var act = () => new FmpMediaSegmentWriter(new MediaTrackSet([track]));

            act.Should().ThrowExactly<ArgumentException>(reason);
        }

        // ---- Media segments ----

        [Fact]
        public void BuildMediaSegment_H264AacFixture_Should_Write_Both_Tracks_Per_Segment()
        {
            var capture = H264AacCapture();
            var video = capture.Packets.Where(p => p.TrackId == FlvDemuxer.VideoTrackId).ToList();
            var audio = capture.Packets.Where(p => p.TrackId == FlvDemuxer.AudioTrackId).ToList();
            // Cut at the second keyframe (DTS 200), as a segmenter would.
            var firstVideo = video.Take(5).ToList();
            var firstAudio = audio.Where(p => p.Dts < 200).ToList();
            var secondVideo = video.Skip(5).ToList();
            var secondAudio = audio.Where(p => p.Dts >= 200).ToList();
            var writer = new FmpMediaSegmentWriter(capture.Demuxer.Tracks!);

            var first = Parse(writer.BuildMediaSegment(
                Interleave(firstVideo, firstAudio),
                new Dictionary<int, long> { [FlvDemuxer.VideoTrackId] = 200, [FlvDemuxer.AudioTrackId] = secondAudio[0].Dts }));
            var second = Parse(writer.BuildMediaSegment(Interleave(secondVideo, secondAudio)));

            first.SequenceNumber.Should().Be(1);
            second.SequenceNumber.Should().Be(2);
            first.Trafs.Select(t => t.TrackId).Should().Equal(1u, 2u);

            var firstVideoTraf = first.Trafs[0];
            firstVideoTraf.BaseMediaDecodeTime.Should().Be(0);
            firstVideoTraf.Version.Should().Be(1, "B-frames need signed composition offsets");
            firstVideoTraf.Flags.Should().Be(0x000F01);
            firstVideoTraf.Durations.Should().Equal(40u, 40u, 40u, 40u, 40u);
            firstVideoTraf.Sizes.Should().Equal(firstVideo.Select(p => (uint)p.Payload.Length));
            firstVideoTraf.SampleFlags.Should().Equal(SyncFlags, NonSyncFlags, NonSyncFlags, NonSyncFlags, NonSyncFlags);
            firstVideoTraf.CompositionOffsets.Should().Equal(80, 160, 40, 40, 80);

            var firstAudioTraf = first.Trafs[1];
            firstAudioTraf.BaseMediaDecodeTime.Should().Be(57);
            firstAudioTraf.Flags.Should().Be(0x000301, "audio needs no sample flags or composition offsets");
            firstAudioTraf.Version.Should().Be(0);
            firstAudioTraf.Durations.Should().Equal(23u, 23u, 23u, 24u, 23u, 23u, 23u);

            var secondVideoTraf = second.Trafs[0];
            secondVideoTraf.BaseMediaDecodeTime.Should().Be(200);
            secondVideoTraf.Durations.Should().Equal([40u, 40u, 40u, 40u, 40u], "with no hint, the last sample repeats the previous duration");
            secondVideoTraf.SampleFlags[0].Should().Be(SyncFlags);
            second.Trafs[1].BaseMediaDecodeTime.Should().Be(219);

            // Every trun's data_offset points at its own samples, video's then audio's, in mdat.
            foreach (var parsed in new[] { first, second })
            {
                var expected = new List<byte>();
                foreach (var traf in parsed.Trafs)
                {
                    var start = (int)traf.DataOffset;
                    var length = traf.Sizes.Sum(s => (int)s);
                    expected.AddRange(parsed.Bytes.AsSpan(start, length).ToArray());
                }

                expected.Should().Equal(parsed.MdatContent);
            }

            first.MdatContent.Should().Equal(firstVideo.Concat(firstAudio).SelectMany(p => p.Payload.ToArray()));
        }

        [Fact]
        public void BuildMediaSegment_VideoWithoutCompositionOffsets_Should_Use_Version0_Trun_Without_Offsets()
        {
            var writer = new FmpMediaSegmentWriter(new MediaTrackSet([VideoTrack()]));

            var parsed = Parse(writer.BuildMediaSegment([Video(0, key: true), Video(40), Video(80)]));

            var traf = parsed.Trafs.Single();
            traf.Version.Should().Be(0);
            traf.Flags.Should().Be(0x000701);
            traf.CompositionOffsets.Should().BeEmpty();
            traf.SampleFlags.Should().Equal(SyncFlags, NonSyncFlags, NonSyncFlags);
        }

        [Fact]
        public void BuildMediaSegment_NegativeCompositionOffset_Should_Be_Written_Signed()
        {
            var writer = new FmpMediaSegmentWriter(new MediaTrackSet([VideoTrack()]));

            var parsed = Parse(writer.BuildMediaSegment([Video(100, key: true, pts: 60)]));

            parsed.Trafs.Single().CompositionOffsets.Should().Equal(-40);
        }

        [Fact]
        public void BuildMediaSegment_TrackWithNoPackets_Should_Get_No_Traf()
        {
            var writer = new FmpMediaSegmentWriter(new MediaTrackSet([VideoTrack(), AudioTrack()]));

            var parsed = Parse(writer.BuildMediaSegment([Audio(0), Audio(23)]));

            parsed.Trafs.Select(t => t.TrackId).Should().Equal(2u);
            parsed.Trafs[0].DataOffset.Should().Be((uint)(parsed.MoofSize + 8));
        }

        [Fact]
        public void BuildMediaSegment_LastDuration_Should_Fall_Back_To_The_Previous_Segment_Then_The_Default()
        {
            var writer = new FmpMediaSegmentWriter(new MediaTrackSet([VideoTrack(), AudioTrack()]));

            var first = Parse(writer.BuildMediaSegment([Video(0, key: true), Audio(0)]));
            var second = Parse(writer.BuildMediaSegment([Video(50, key: true), Video(90), Audio(23), Audio(46)]));
            var third = Parse(writer.BuildMediaSegment([Video(100, key: true), Audio(69)]));

            first.Trafs[0].Durations.Should().Equal([33u], "1/30 s at timescale 1000");
            first.Trafs[1].Durations.Should().Equal([23u], "1024 samples at 44100 Hz, in milliseconds");
            second.Trafs[0].Durations.Should().Equal(40u, 40u);
            second.Trafs[1].Durations.Should().Equal(23u, 23u);
            third.Trafs[0].Durations.Should().Equal([40u], "the previous segment's last duration");
            third.Trafs[0].BaseMediaDecodeTime.Should().Be(100, "tfdt always comes from the real DTS");
        }

        [Fact]
        public void BuildMediaSegment_CoarseTimescale_Should_Never_Default_To_A_Zero_Duration()
        {
            var track = VideoTrack() with { Timescale = 1 };
            var writer = new FmpMediaSegmentWriter(new MediaTrackSet([track]));

            var parsed = Parse(writer.BuildMediaSegment([Video(0, key: true)]));

            parsed.Trafs.Single().Durations.Should().Equal(1u);
        }

        [Fact]
        public void BuildMediaSegment_HintForTrackWithoutPackets_Should_Be_Ignored()
        {
            var writer = new FmpMediaSegmentWriter(new MediaTrackSet([VideoTrack(), AudioTrack()]));

            var parsed = Parse(writer.BuildMediaSegment([Audio(0)], new Dictionary<int, long> { [0] = -5, [99] = 0 }));

            parsed.Trafs.Single().Durations.Should().Equal(23u);
        }

        [Fact]
        public void BuildMediaSegment_PacketMode_Should_Advance_The_Sequence_Number()
        {
            var writer = new FmpMediaSegmentWriter(new MediaTrackSet([AudioTrack()]));

            Parse(writer.BuildMediaSegment([Audio(0)])).SequenceNumber.Should().Be(1);
            Parse(writer.BuildMediaSegment([Audio(23)])).SequenceNumber.Should().Be(2);
        }

        public static TheoryData<string, MediaPacket[], Dictionary<int, long>?> InvalidSegments => new()
        {
            { "no packets", [], null },
            { "unknown track", [new MediaPacket(7, 0, 0, true, 0, new byte[] { 1 })], null },
            { "config version", [new MediaPacket(0, 0, 0, true, 1, new byte[] { 1 })], null },
            { "negative DTS", [Video(-1, key: true)], null },
            { "repeated DTS", [Video(0, key: true), Video(0)], null },
            { "DTS going backwards", [Video(40, key: true), Video(0)], null },
            { "offset past int", [Video(0, key: true, pts: (long)int.MaxValue + 1)], null },
            { "negative offset past int", [Video(int.MaxValue + 2L, key: true, pts: 0)], null },
            { "duration past uint", [Video(0, key: true), Video(uint.MaxValue + 1L)], null },
            { "hint duration past uint", [Video(0, key: true)], new Dictionary<int, long> { [0] = uint.MaxValue + 1L } },
            { "hint not after last DTS", [Video(0, key: true), Video(40)], new Dictionary<int, long> { [0] = 40 } },
        };

        [Theory]
        [MemberData(nameof(InvalidSegments))]
        public void BuildMediaSegment_InvalidPackets_Should_Throw_And_Leave_The_Writer_Unchanged(string reason, MediaPacket[] packets, Dictionary<int, long>? hints)
        {
            var writer = new FmpMediaSegmentWriter(new MediaTrackSet([VideoTrack()]));

            var act = () => writer.BuildMediaSegment(packets, hints);

            act.Should().ThrowExactly<ArgumentException>(reason).Which.ParamName.Should().Be(reason.StartsWith("hint") ? "nextDecodeTimes" : "packets");
            var next = Parse(writer.BuildMediaSegment([Video(0, key: true)]));
            next.SequenceNumber.Should().Be(1, "a failed call writes nothing");
        }

        [Fact]
        public void BuildMediaSegment_DtsNotAfterThePreviousSegment_Should_Throw()
        {
            var writer = new FmpMediaSegmentWriter(new MediaTrackSet([VideoTrack()]));
            writer.BuildMediaSegment([Video(0, key: true), Video(40)]);

            var act = () => writer.BuildMediaSegment([Video(40, key: true)]);

            act.Should().ThrowExactly<ArgumentException>();
            Parse(writer.BuildMediaSegment([Video(80, key: true)])).SequenceNumber.Should().Be(2);
        }

        [Fact]
        public void BuildMediaSegment_FailedCall_Should_Not_Record_Its_Timestamps()
        {
            var writer = new FmpMediaSegmentWriter(new MediaTrackSet([VideoTrack(), AudioTrack()]));

            // The audio packets are fine, the video one fails: nothing may be remembered.
            var act = () => writer.BuildMediaSegment([Audio(500), Video(-1, key: true)]);
            act.Should().ThrowExactly<ArgumentException>();

            Parse(writer.BuildMediaSegment([Audio(0)])).Trafs.Single().BaseMediaDecodeTime.Should().Be(0);
        }

        [Fact]
        public void BuildMediaSegment_NullPackets_Should_Throw()
        {
            var writer = new FmpMediaSegmentWriter(new MediaTrackSet([VideoTrack()]));

            var act = () => writer.BuildMediaSegment((IReadOnlyList<MediaPacket>)null!);

            act.Should().ThrowExactly<ArgumentNullException>();
        }

        [Fact]
        public void BuildMediaSegment_PacketsOnFrameModeWriter_Should_Throw()
        {
            var writer = new FmpMediaSegmentWriter();

            var act = () => writer.BuildMediaSegment([Audio(0)]);

            act.Should().ThrowExactly<InvalidOperationException>();
        }

        [Fact]
        public void BuildMediaSegment_FramesOnPacketModeWriter_Should_Throw()
        {
            var writer = new FmpMediaSegmentWriter(new MediaTrackSet([AudioTrack()]));

            var act = () => writer.BuildMediaSegment([new Mp4EncodedFrame([1], 1024)]);

            act.Should().ThrowExactly<InvalidOperationException>();
        }

        [Fact]
        public void Constructor_Null_Should_Throw()
        {
            var act = () => new FmpMediaSegmentWriter(null!);

            act.Should().ThrowExactly<ArgumentNullException>();
        }

        [Fact]
        public void Constructor_EmptyTrackSet_Should_Throw()
        {
            var act = () => new FmpMediaSegmentWriter(new MediaTrackSet([]));

            act.Should().ThrowExactly<ArgumentException>();
        }

        [Fact]
        public void Constructor_UnsupportedCodec_Should_Throw()
        {
            var track = new MediaTrackInfo(2, MediaKind.Audio, MediaCodec.Mp3, 1000, ReadOnlyMemory<byte>.Empty, 0, 44100, 2);

            var act = () => new FmpMediaSegmentWriter(new MediaTrackSet([AudioTrack(), track]));

            act.Should().ThrowExactly<NotSupportedException>();
        }

        // Env-gated cross-check: set EGGENCODER_FFMPEG_ORACLE=1 with ffmpeg/ffprobe on PATH to run
        // it; passes trivially otherwise, so CI needs no ffmpeg.
        [Fact]
        public void Ffmpeg_OnInitPlusSegments_Should_Report_And_Decode_Both_Streams()
        {
            if (Environment.GetEnvironmentVariable("EGGENCODER_FFMPEG_ORACLE") != "1")
            {
                return;
            }

            var capture = H264AacCapture();
            var tracks = capture.Demuxer.Tracks!;
            var writer = new FmpMediaSegmentWriter(tracks);
            var bytes = new List<byte>(FmpInitSegmentWriter.BuildInitSegment(tracks));
            bytes.AddRange(writer.BuildMediaSegment(capture.Packets.Where(p => p.Dts < 200).ToList()));
            bytes.AddRange(writer.BuildMediaSegment(capture.Packets.Where(p => p.Dts >= 200).ToList()));
            var path = Path.Combine(Path.GetTempPath(), $"eggencoder-fmp4-{Guid.NewGuid():N}.mp4");
            try
            {
                File.WriteAllBytes(path, bytes.ToArray());

                var (probeExit, probeOutput, _) = Run("ffprobe", $"-v error -show_entries stream=codec_name,width,height,sample_rate,channels -show_entries packet=stream_index -of compact=p=0 \"{path}\"");
                probeExit.Should().Be(0);
                probeOutput.Should().Contain("codec_name=h264|width=64|height=64");
                probeOutput.Should().Contain("codec_name=aac|sample_rate=44100|channels=1");
                probeOutput.Split('\n').Count(l => l.StartsWith("stream_index=")).Should().Be(capture.Packets.Count);

                var (decodeExit, _, decodeErrors) = Run("ffmpeg", $"-v error -i \"{path}\" -f null -");
                decodeExit.Should().Be(0);
                decodeErrors.Should().BeEmpty();
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static (int ExitCode, string Output, string Errors) Run(string fileName, string arguments)
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

        private static FlvDemuxerTest.Capture H264AacCapture() =>
            FlvDemuxerTest.Demux(File.ReadAllBytes(FlvDemuxerTest.H264AacFixture));

        private static MediaTrackInfo VideoTrack() =>
            new(0, MediaKind.Video, MediaCodec.H264, 1000, FlvTestBuilder.AvcC, 0, width: 64, height: 64);

        private static MediaTrackInfo AudioTrack() =>
            new(1, MediaKind.Audio, MediaCodec.Aac, 1000, FlvTestBuilder.AacConfig, 0, 44100, 1);

        private static MediaPacket Video(long dts, bool key = false, long? pts = null) =>
            new(0, pts ?? dts, dts, key, 0, new byte[] { 0, 0, 0, 1, (byte)(key ? 0x65 : 0x41) });

        private static MediaPacket Audio(long dts) => new(1, dts, dts, true, 0, new byte[] { 0x21, 0x10 });

        private static List<MediaPacket> Interleave(List<MediaPacket> video, List<MediaPacket> audio) =>
            video.Concat(audio).OrderBy(p => p.Dts).ToList();

        private static ParsedTrak ReadSingleTrak(byte[] init)
        {
            using var stream = new MemoryStream(init);
            return ReadSingleTrak(stream);
        }

        private static ParsedTrak ReadSingleTrak(MemoryStream stream)
        {
            var moov = MovAtomReader.EnumerateAtoms(stream, 0, stream.Length).Single(a => a.Type == "moov");
            return ReadTrak(stream, MovAtomReader.EnumerateAtoms(stream, moov.ContentStart, moov.ContentEnd).Single(a => a.Type == "trak"));
        }

        private static ParsedTrak ReadTrak(Stream stream, MovAtom trak)
        {
            var tkhd = MovAtomReader.FindAtom(stream, "tkhd", trak.ContentStart, trak.ContentEnd)!.Value;
            var mdia = MovAtomReader.FindAtom(stream, "mdia", trak.ContentStart, trak.ContentEnd)!.Value;
            var mdhd = MovAtomReader.FindAtom(stream, "mdhd", mdia.ContentStart, mdia.ContentEnd)!.Value;
            var hdlr = MovAtomReader.FindAtom(stream, "hdlr", mdia.ContentStart, mdia.ContentEnd)!.Value;
            var minf = MovAtomReader.FindAtom(stream, "minf", mdia.ContentStart, mdia.ContentEnd)!.Value;
            var minfChildren = MovAtomReader.EnumerateAtoms(stream, minf.ContentStart, minf.ContentEnd);
            var stbl = minfChildren.Single(a => a.Type == "stbl");
            var stsd = MovAtomReader.FindAtom(stream, "stsd", stbl.ContentStart, stbl.ContentEnd)!.Value;
            var entry = MovAtomReader.EnumerateAtoms(stream, stsd.ContentStart + 8, stsd.ContentEnd).Single();

            return new ParsedTrak(
                TrackId: ReadUInt32(stream, tkhd.ContentStart + 12),
                TkhdVolume: ReadUInt16(stream, tkhd.ContentStart + 36),
                TkhdWidth: ReadUInt32(stream, tkhd.ContentStart + 76),
                TkhdHeight: ReadUInt32(stream, tkhd.ContentStart + 80),
                Timescale: ReadUInt32(stream, mdhd.ContentStart + 12),
                Handler: ReadFourCc(stream, hdlr.ContentStart + 8),
                MediaHeader: minfChildren[0].Type,
                SampleEntry: entry.Type,
                SampleEntryContentStart: entry.ContentStart,
                SampleEntryContentEnd: entry.ContentEnd);
        }

        private static byte[] ReadChildBox(Stream stream, long start, long end, string type)
        {
            var box = MovAtomReader.FindAtom(stream, type, start, end)!.Value;
            var content = new byte[box.ContentEnd - box.ContentStart];
            stream.Position = box.ContentStart;
            stream.ReadExactly(content);
            return content;
        }

        // A test-only multi-traf moof/trun reader (see FmpSegmentWriterTest.ParseMediaSegment for
        // why this isn't production code), extended with trun's version, sample flags and signed
        // composition offsets.
        private static ParsedSegment Parse(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes);
            var topLevel = MovAtomReader.EnumerateAtoms(stream, 0, stream.Length);
            var moof = topLevel.Single(a => a.Type == "moof");
            var mfhd = MovAtomReader.FindAtom(stream, "mfhd", moof.ContentStart, moof.ContentEnd)!.Value;

            var trafs = new List<ParsedTraf>();
            foreach (var traf in MovAtomReader.EnumerateAtoms(stream, moof.ContentStart, moof.ContentEnd).Where(a => a.Type == "traf"))
            {
                var tfhd = MovAtomReader.FindAtom(stream, "tfhd", traf.ContentStart, traf.ContentEnd)!.Value;
                ReadUInt32(stream, tfhd.ContentStart).Should().Be(0x020000, "default-base-is-moof");
                var tfdt = MovAtomReader.FindAtom(stream, "tfdt", traf.ContentStart, traf.ContentEnd)!.Value;
                var trun = MovAtomReader.FindAtom(stream, "trun", traf.ContentStart, traf.ContentEnd)!.Value;
                var versionAndFlags = ReadUInt32(stream, trun.ContentStart);
                var flags = versionAndFlags & 0xFFFFFF;
                var count = ReadUInt32(stream, trun.ContentStart + 4);
                var parsed = new ParsedTraf(
                    TrackId: ReadUInt32(stream, tfhd.ContentStart + 4),
                    BaseMediaDecodeTime: (long)ReadUInt64(stream, tfdt.ContentStart + 4),
                    Version: (int)(versionAndFlags >> 24),
                    Flags: flags,
                    DataOffset: ReadUInt32(stream, trun.ContentStart + 8));

                var position = trun.ContentStart + 12;
                for (var i = 0; i < count; i++)
                {
                    parsed.Durations.Add(ReadUInt32(stream, position));
                    parsed.Sizes.Add(ReadUInt32(stream, position + 4));
                    position += 8;
                    if ((flags & 0x400) != 0)
                    {
                        parsed.SampleFlags.Add(ReadUInt32(stream, position));
                        position += 4;
                    }

                    if ((flags & 0x800) != 0)
                    {
                        parsed.CompositionOffsets.Add((int)ReadUInt32(stream, position));
                        position += 4;
                    }
                }

                position.Should().Be(trun.ContentEnd, "the trun's entries fill it exactly");
                trafs.Add(parsed);
            }

            var mdat = topLevel.Single(a => a.Type == "mdat");
            var mdatContent = bytes[(int)mdat.ContentStart..(int)mdat.ContentEnd];
            return new ParsedSegment(bytes, ReadUInt32(stream, mfhd.ContentStart + 4), moof.ContentEnd - moof.ContentStart + 8, trafs, mdatContent);
        }

        private static ushort ReadUInt16(Stream stream, long position)
        {
            stream.Position = position;
            Span<byte> buffer = stackalloc byte[2];
            stream.ReadExactly(buffer);
            return BinaryPrimitives.ReadUInt16BigEndian(buffer);
        }

        private static uint ReadUInt32(Stream stream, long position)
        {
            stream.Position = position;
            Span<byte> buffer = stackalloc byte[4];
            stream.ReadExactly(buffer);
            return BinaryPrimitives.ReadUInt32BigEndian(buffer);
        }

        private static ulong ReadUInt64(Stream stream, long position)
        {
            stream.Position = position;
            Span<byte> buffer = stackalloc byte[8];
            stream.ReadExactly(buffer);
            return BinaryPrimitives.ReadUInt64BigEndian(buffer);
        }

        private static string ReadFourCc(Stream stream, long position)
        {
            stream.Position = position;
            Span<byte> buffer = stackalloc byte[4];
            stream.ReadExactly(buffer);
            return Encoding.ASCII.GetString(buffer);
        }

        private sealed record ParsedTrak(
            uint TrackId,
            ushort TkhdVolume,
            uint TkhdWidth,
            uint TkhdHeight,
            uint Timescale,
            string Handler,
            string MediaHeader,
            string SampleEntry,
            long SampleEntryContentStart,
            long SampleEntryContentEnd);

        private sealed record ParsedTraf(uint TrackId, long BaseMediaDecodeTime, int Version, uint Flags, uint DataOffset)
        {
            public List<uint> Durations { get; } = [];
            public List<uint> Sizes { get; } = [];
            public List<uint> SampleFlags { get; } = [];
            public List<int> CompositionOffsets { get; } = [];
        }

        private sealed record ParsedSegment(byte[] Bytes, uint SequenceNumber, long MoofSize, List<ParsedTraf> Trafs, byte[] MdatContent);
    }
}
