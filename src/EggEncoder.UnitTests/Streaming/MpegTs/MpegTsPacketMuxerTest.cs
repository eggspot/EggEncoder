using System.Diagnostics;
using EggEncoder.Containers.Flv;
using EggEncoder.Media;
using EggEncoder.Streaming.Mp4;
using EggEncoder.Streaming.MpegTs;
using EggEncoder.UnitTests.Containers.Flv;
using EggEncoder.UnitTests.Media;
using FluentAssertions;
using static EggEncoder.UnitTests.Streaming.MpegTs.MpegTsTestReader;

namespace EggEncoder.UnitTests.Streaming.MpegTs
{
    // The packet-model (MediaTrackSet/MediaPacket) half of MpegTsMuxer:
    // docs/live-ingest-restream-plan.md item 5b. The frame-mode half stays in MpegTsMuxerTest.
    public class MpegTsPacketMuxerTest
    {
        private const int VideoPid = 0x100;
        private const int AudioPid = 0x101;
        private const long PcrDelay90k = 63000;

        private static readonly byte[] H264Aud = [0, 0, 0, 1, 0x09, 0xF0];
        private static readonly byte[] H265Aud = [0, 0, 0, 1, 0x46, 0x01, 0x50];

        // ---- Fixture: H.264 + AAC from FLV ----

        [Fact]
        public void BuildSegment_H264AacFixture_Should_Write_Pat_Pmt_And_One_Pes_Per_Packet()
        {
            var capture = H264AacCapture();
            var muxer = new MpegTsMuxer(capture.Demuxer.Tracks!);

            var packets = ReadPackets(muxer.BuildSegment(capture.Packets));

            ReadPat(packets[0]).Should().Be(0x1000);
            packets[1].Pid.Should().Be(0x1000);
            var (pcrPid, streams) = ReadPmt(packets[1]);
            pcrPid.Should().Be(VideoPid, "the PCR rides on the video track");
            streams.Should().Equal(((byte)0x1B, VideoPid), ((byte)0x0F, AudioPid));
            packets.Select(p => p.Pid).Distinct().Should().BeEquivalentTo([0, 0x1000, VideoPid, AudioPid]);

            var video = ReadPes(packets, VideoPid);
            var audio = ReadPes(packets, AudioPid);
            var sourceVideo = capture.Packets.Where(p => p.TrackId == FlvDemuxer.VideoTrackId).ToList();
            var sourceAudio = capture.Packets.Where(p => p.TrackId == FlvDemuxer.AudioTrackId).ToList();
            video.Should().HaveCount(sourceVideo.Count);
            audio.Should().HaveCount(sourceAudio.Count);

            for (var i = 0; i < video.Count; i++)
            {
                var source = sourceVideo[i];
                video[i].StreamId.Should().Be(0xE0);
                video[i].PesPacketLength.Should().Be(0, "video PES lengths are left unbounded");
                video[i].DataAlignment.Should().BeTrue();
                video[i].Pts.Should().Be(source.Pts * 90);
                video[i].Dts.Should().Be(source.Dts * 90, "every fixture frame has a composition offset, so DTS is written");
                video[i].First.RandomAccess.Should().Be(source.IsKeyframe);
                video[i].Es.Should().Equal(ExpectedH264Es(source));
            }

            for (var i = 0; i < audio.Count; i++)
            {
                var source = sourceAudio[i];
                audio[i].StreamId.Should().Be(0xC0);
                audio[i].DataAlignment.Should().BeFalse();
                audio[i].Pts.Should().Be(source.Pts * 90);
                audio[i].Dts.Should().BeNull("audio PTS equals DTS, so only the PTS is written");
                audio[i].First.RandomAccess.Should().BeTrue("every AAC frame is a random access point");
                audio[i].Es.Take(7).Should().Equal(AdtsHeader(profile: 1, frequencyIndex: 4, channels: 1, source.Payload.Length));
                audio[i].Es.Skip(7).Should().Equal(source.Payload.ToArray());
            }
        }

        [Fact]
        public void BuildSegment_Should_Write_Pes_In_Dts_Order_Whatever_The_Input_Order()
        {
            var capture = H264AacCapture();
            var videoFirst = capture.Packets.OrderBy(p => p.TrackId).ToList();

            var packets = ReadPackets(new MpegTsMuxer(capture.Demuxer.Tracks!).BuildSegment(videoFirst));

            var decodeTimes = packets.Where(p => p.PayloadUnitStart && p.Pid is VideoPid or AudioPid)
                .Select(p => ReadPes(packets, p.Pid).Single(pes => pes.First == p).DecodeTime)
                .ToList();
            decodeTimes.Should().BeInAscendingOrder();
            decodeTimes.Should().HaveCount(capture.Packets.Count);
        }

        [Fact]
        public void BuildSegment_Pcr_Should_Run_700ms_Behind_Dts_Clamped_At_Zero_And_Never_Pass_A_Later_Dts()
        {
            var capture = H264AacCapture();
            var muxer = new MpegTsMuxer(capture.Demuxer.Tracks!);

            var packets = ReadPackets(muxer.BuildSegment(capture.Packets));

            var video = ReadPes(packets, VideoPid);
            foreach (var pes in video)
            {
                pes.First.Pcr.Should().Be(Math.Max(0, pes.Dts!.Value - PcrDelay90k) * 300, "every video PES carries a PCR");
            }

            packets.Where(p => p.Pid == AudioPid).Should().OnlyContain(p => p.Pcr == null);
            packets.Should().NotContain(p => p.AdaptationFieldControl == 0x2, "40 ms video frames never leave a 100 ms PCR gap");

            long lastPcr = -1;
            var audio = ReadPes(packets, AudioPid);
            foreach (var packet in packets)
            {
                if (packet.Pcr is { } pcr)
                {
                    pcr.Should().BeGreaterThanOrEqualTo(lastPcr);
                    lastPcr = pcr;
                }

                if (packet.PayloadUnitStart && packet.Pid is VideoPid or AudioPid)
                {
                    var pes = (packet.Pid == VideoPid ? video : audio).Single(p => p.First == packet);
                    (pes.DecodeTime * 300).Should().BeGreaterThanOrEqualTo(lastPcr, "no PES arrives after its decode time");
                }
            }
        }

        [Fact]
        public void BuildSegment_Should_Stuff_With_Adaptation_Fields_And_Continue_Continuity_Counters_Across_Segments()
        {
            var capture = H264AacCapture();
            var muxer = new MpegTsMuxer(capture.Demuxer.Tracks!);

            var first = ReadPackets(muxer.BuildSegment(capture.Packets.Where(p => p.Dts < 200).ToList()));
            var second = ReadPackets(muxer.BuildSegment(capture.Packets.Where(p => p.Dts >= 200).ToList()));

            ReadPat(second[0]).Should().Be(0x1000, "every segment starts with PAT and PMT");
            ReadPmt(second[1]).Streams.Should().HaveCount(2);
            ReadPes(second, VideoPid)[0].First.RandomAccess.Should().BeTrue();

            var lastCounter = new Dictionary<int, int>();
            foreach (var packet in first.Concat(second))
            {
                if (lastCounter.TryGetValue(packet.Pid, out var last))
                {
                    var expected = packet.Payload.Length > 0 ? (last + 1) & 0x0F : last;
                    packet.ContinuityCounter.Should().Be(expected, $"PID {packet.Pid:X} counts on from the previous segment");
                }
                else
                {
                    packet.ContinuityCounter.Should().Be(0);
                }

                lastCounter[packet.Pid] = packet.ContinuityCounter;
            }

            // ReadPackets checked every adaptation field's stuffing; ReadPes checked every audio
            // PES length against its bytes. Video PES lengths are 0, so trailing bytes would show
            // up in the elementary stream instead:
            ReadPes(second, VideoPid).Select(p => p.Es.Length).Should().Equal(
                capture.Packets.Where(p => p.TrackId == FlvDemuxer.VideoTrackId && p.Dts >= 200).Select(p => ExpectedH264Es(p).Length));
        }

        [Fact]
        public void BuildSegment_Hevc_Should_Write_Stream_Type_0x24_With_Aud_And_Parameter_Sets()
        {
            var capture = FlvDemuxerTest.Demux(File.ReadAllBytes(FlvDemuxerTest.HevcFixture));
            var tracks = capture.Demuxer.Tracks!;
            var record = HevcDecoderConfigurationRecord.Parse(tracks.Tracks[0].CodecConfiguration.Span);
            var muxer = new MpegTsMuxer(tracks);

            var packets = ReadPackets(muxer.BuildSegment(capture.Packets));

            ReadPmt(packets[1]).Streams.Should().Equal(((byte)0x24, VideoPid));
            var video = ReadPes(packets, VideoPid);
            video.Should().HaveCount(capture.Packets.Count);
            for (var i = 0; i < video.Count; i++)
            {
                var source = capture.Packets[i];
                var nalTypes = Nals(source.Payload.ToArray()).Select(n => (n[0] >> 1) & 0x3F).ToList();
                var expected = new List<byte>(H265Aud);
                if (source.IsKeyframe && !nalTypes.Contains(33))
                {
                    foreach (var type in new[] { 32, 33, 34 })
                    {
                        foreach (var nal in record.GetNalUnits(type))
                        {
                            expected.AddRange(AnnexB(nal.ToArray()));
                        }
                    }
                }

                expected.AddRange(AnnexBConverter.ToAnnexB(source.Payload.Span));
                video[i].Es.Should().Equal(expected);
            }

            video.Count(v => v.First.RandomAccess).Should().Be(capture.Packets.Count(p => p.IsKeyframe));
        }

        // ---- Video access units ----

        [Fact]
        public void BuildSegment_KeyframeCarryingItsOwnSps_Should_Not_Repeat_The_Parameter_Sets()
        {
            byte[] sps = [0x67, 0x4D, 0x40, 0x0A];
            byte[] pps = [0x68, 0xCE];
            byte[] idr = [0x65, 0x88, 0x84];
            var muxer = new MpegTsMuxer(new MediaTrackSet([VideoTrack()]));

            var es = SingleVideoEs(muxer.BuildSegment([Video(0, key: true, LengthPrefixed(sps, pps, idr))]));

            es.Should().Equal([.. H264Aud, .. AnnexB(sps), .. AnnexB(pps), .. AnnexB(idr)]);
        }

        [Fact]
        public void BuildSegment_AccessUnitStartingWithAud_Should_Keep_It_First_And_Insert_Parameter_Sets_After_It()
        {
            byte[] aud = [0x09, 0x10];
            byte[] idr = [0x65, 0x88];
            var record = AvcDecoderConfigurationRecord.Parse(FlvTestBuilder.AvcC);
            var muxer = new MpegTsMuxer(new MediaTrackSet([VideoTrack()]));

            var es = SingleVideoEs(muxer.BuildSegment([Video(0, key: true, LengthPrefixed(aud, idr))]));

            es.Should().Equal([
                .. AnnexB(aud),
                .. AnnexB(record.SequenceParameterSets[0].ToArray()),
                .. AnnexB(record.PictureParameterSets[0].ToArray()),
                .. AnnexB(idr)]);
        }

        [Fact]
        public void BuildSegment_NonKeyframe_Should_Get_Only_An_Aud()
        {
            byte[] slice = [0x41, 0x9A];
            var muxer = new MpegTsMuxer(new MediaTrackSet([VideoTrack()]));

            var es = SingleVideoEs(muxer.BuildSegment([Video(0, key: false, LengthPrefixed(slice))]));

            es.Should().Equal([.. H264Aud, .. AnnexB(slice)]);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void BuildSegment_ShortNalLengthPrefixes_Should_Be_Read_With_The_Records_Length_Size(int nalLengthSize)
        {
            var avcC = (byte[])FlvTestBuilder.AvcC.Clone();
            avcC[4] = (byte)(0xFC | (nalLengthSize - 1));
            var track = new MediaTrackInfo(0, MediaKind.Video, MediaCodec.H264, 1000, avcC, 0);
            byte[] slice = [0x41, 0x9A, 0x01];
            byte[] payload = nalLengthSize == 1 ? [3, .. slice] : [0, 3, .. slice];

            var es = SingleVideoEs(new MpegTsMuxer(new MediaTrackSet([track])).BuildSegment([Video(0, key: false, payload)]));

            es.Should().Equal([.. H264Aud, .. AnnexB(slice)]);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(100)]
        [InlineData(155)]
        [InlineData(156)]
        [InlineData(157)]
        [InlineData(158)]
        [InlineData(159)]
        [InlineData(160)]
        [InlineData(338)]
        [InlineData(339)]
        [InlineData(340)]
        [InlineData(5000)]
        public void BuildSegment_AnyPesLength_Should_Fill_The_Last_Packet_With_Adaptation_Field_Stuffing(int nalLength)
        {
            // A second video track carries neither PCR nor (for a non-keyframe) a random access
            // flag, so its PES fill packets exactly or need 1 (adaptation_field_length 0) or more
            // stuffing bytes: PES header 14 + AUD 6 + start code 4 + nalLength = 184 at 160.
            var tracks = new MediaTrackSet([VideoTrack(), VideoTrack(1)]);
            var nal = Enumerable.Range(0, nalLength).Select(i => (byte)(i == 0 ? 0x41 : i % 251)).ToArray();

            var packets = ReadPackets(new MpegTsMuxer(tracks).BuildSegment([new MediaPacket(1, 0, 0, false, 0, LengthPrefixed(nal))]));

            var pes = ReadPes(packets, AudioPid).Single();
            pes.StreamId.Should().Be(0xE1, "the second video track gets the next video stream id");
            pes.Es.Should().Equal([.. H264Aud, .. AnnexB(nal)]);
            var secondTrackPackets = packets.Where(p => p.Pid == AudioPid).ToList();
            secondTrackPackets.Take(secondTrackPackets.Count - 1).Should().NotContain(p => p.AdaptationFieldLength != null, "only the last packet needs stuffing");
            var pesLength = 14 + 6 + 4 + nalLength;
            secondTrackPackets.Last().AdaptationFieldLength.Should().Be(pesLength % 184 == 0 ? null : 184 - (pesLength % 184) - 1);
            var (pcrPid, streams) = ReadPmt(packets[1]);
            pcrPid.Should().Be(VideoPid);
            streams.Should().Equal(((byte)0x1B, VideoPid), ((byte)0x1B, AudioPid));
        }

        [Fact]
        public void BuildSegment_VideoWithoutCompositionOffset_Should_Write_Pts_Only()
        {
            var muxer = new MpegTsMuxer(new MediaTrackSet([VideoTrack()]));

            var pes = ReadPes(ReadPackets(muxer.BuildSegment([Video(40, key: true)])), VideoPid).Single();

            pes.Pts.Should().Be(3600);
            pes.Dts.Should().BeNull();
        }

        // ---- Timestamps and PCR ----

        [Fact]
        public void BuildSegment_Should_Rescale_Timestamps_From_The_Track_Timescale_To_90kHz()
        {
            var track = new MediaTrackInfo(7, MediaKind.Audio, MediaCodec.Aac, 48000, Convert.FromHexString("1190"), 0);
            var muxer = new MpegTsMuxer(new MediaTrackSet([track]));

            var pes = ReadPes(ReadPackets(muxer.BuildSegment([new MediaPacket(7, 1024, 1024, true, 0, new byte[] { 0x21 })])), VideoPid).Single();

            pes.Pts.Should().Be(1920);
            pes.Es.Take(7).Should().Equal(AdtsHeader(profile: 1, frequencyIndex: 3, channels: 2, payloadLength: 1));
        }

        [Fact]
        public void BuildSegment_TimestampsPast33Bits_Should_Wrap_Like_Pcr()
        {
            var track = new MediaTrackInfo(0, MediaKind.Video, MediaCodec.H264, 90000, FlvTestBuilder.AvcC, 0);
            const long dts = (1L << 33) + 900000;
            var muxer = new MpegTsMuxer(new MediaTrackSet([track]));

            var packets = ReadPackets(muxer.BuildSegment([new MediaPacket(0, dts + 3000, dts, true, 0, LengthPrefixed([0x65, 0x01]))]));

            var pes = ReadPes(packets, VideoPid).Single();
            pes.Dts.Should().Be(900000);
            pes.Pts.Should().Be(903000);
            pes.First.Pcr.Should().Be((900000 - PcrDelay90k) * 300);
        }

        [Fact]
        public void BuildSegment_SparsePcrTrack_Should_Get_Pcr_Only_Packets_Between_Its_Pes()
        {
            // Video at 2 fps, audio every 50 ms: PCRs must still come at least every 100 ms.
            var muxer = new MpegTsMuxer(new MediaTrackSet([VideoTrack(), AudioTrack()]));
            var packets = new List<MediaPacket> { Video(0, key: true), Video(500, key: false) };
            for (var t = 0; t < 1000; t += 50)
            {
                packets.Add(Audio(t));
            }

            var ts = ReadPackets(muxer.BuildSegment(packets));

            var pcrOnly = ts.Where(p => p.AdaptationFieldControl == 0x2).ToList();
            pcrOnly.Should().NotBeEmpty();
            pcrOnly.Should().OnlyContain(p => p.Pid == VideoPid && p.Pcr != null);
            var pcrs = ts.Where(p => p.Pcr != null).Select(p => p.Pcr!.Value / 300).ToList();
            pcrs.Should().BeInAscendingOrder();
            pcrs.Zip(pcrs.Skip(1)).Should().OnlyContain(pair => pair.Second - pair.First <= 9000 || pair.First == 0, "at most 100 ms apart once the clock leaves its clamp");
            // The PCR holds at 0 until DTS passes 700 ms, then a PCR-only packet goes out whenever
            // an audio PES finds the last PCR 100 ms old: at 800 ms and 900 ms of DTS.
            pcrOnly.Select(p => p.Pcr!.Value / 300).Should().Equal((800 * 90) - PcrDelay90k, (900 * 90) - PcrDelay90k);

            // A PCR-only packet repeats its PID's continuity counter: it carries no payload.
            var videoPackets = ts.Where(p => p.Pid == VideoPid).ToList();
            for (var i = 1; i < videoPackets.Count; i++)
            {
                var expected = videoPackets[i].Payload.Length > 0 ? (videoPackets[i - 1].ContinuityCounter + 1) & 0x0F : videoPackets[i - 1].ContinuityCounter;
                videoPackets[i].ContinuityCounter.Should().Be(expected);
            }
        }

        [Fact]
        public void BuildSegment_SegmentStartingWithAudio_Should_Open_With_A_Pcr_Only_Packet()
        {
            var muxer = new MpegTsMuxer(new MediaTrackSet([VideoTrack(), AudioTrack()]));
            muxer.BuildSegment([Video(1000, key: true), Audio(1000)]);

            var ts = ReadPackets(muxer.BuildSegment([Audio(1023), Video(1040, key: true)]));

            ts[2].AdaptationFieldControl.Should().Be(0x2, "the packet right after PAT and PMT");
            ts[2].Pid.Should().Be(VideoPid);
            ts[2].Pcr.Should().Be(((1023 * 90) - PcrDelay90k) * 300);
            ts[2].ContinuityCounter.Should().Be(0, "the video PID's only earlier packet had counter 0");
            ts[3].Pid.Should().Be(AudioPid);
        }

        [Fact]
        public void BuildSegment_FirstSegmentStartingWithAudio_Should_Give_Its_Pcr_Only_Packet_Counter_15()
        {
            var muxer = new MpegTsMuxer(new MediaTrackSet([VideoTrack(), AudioTrack()]));

            var ts = ReadPackets(muxer.BuildSegment([Audio(0), Video(10, key: true)]));

            ts[2].AdaptationFieldControl.Should().Be(0x2);
            ts[2].Pcr.Should().Be(0);
            ts[2].ContinuityCounter.Should().Be(15, "no payload packet precedes it, and the next one is 0");
            ts.First(p => p.Pid == VideoPid && p.Payload.Length > 0).ContinuityCounter.Should().Be(0);
        }

        [Fact]
        public void BuildSegment_AudioOnly_Should_Carry_Pcr_On_The_Audio_Pid()
        {
            var muxer = new MpegTsMuxer(new MediaTrackSet([AudioTrack()]));

            var ts = ReadPackets(muxer.BuildSegment([Audio(0), Audio(1000)]));

            ReadPmt(ts[1]).PcrPid.Should().Be(VideoPid, "the first (and only) track's PID");
            var pes = ReadPes(ts, VideoPid);
            pes.Select(p => p.First.Pcr).Should().Equal(0L, ((90000 - PcrDelay90k) * 300));
            pes.Should().OnlyContain(p => p.First.RandomAccess);
        }

        // ---- AAC ----

        [Theory]
        [InlineData("0a08", 0, 1)]
        [InlineData("1190", 1, 2)]
        public void BuildSegment_Aac_Should_Take_The_Adts_Profile_From_The_Object_Type(string config, int profile, int channels)
        {
            var track = new MediaTrackInfo(0, MediaKind.Audio, MediaCodec.Aac, 1000, Convert.FromHexString(config), 0);
            var payload = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();

            var es = ReadPes(ReadPackets(new MpegTsMuxer(new MediaTrackSet([track])).BuildSegment([new MediaPacket(0, 0, 0, true, 0, payload)])), VideoPid).Single().Es;

            es.Take(7).Should().Equal(AdtsHeader(profile, config == "0a08" ? 4 : 3, channels, payload.Length));
            es.Skip(7).Should().Equal(payload);
        }

        [Fact]
        public void BuildSegment_AacPayloadAtTheAdtsLimit_Should_Be_Accepted()
        {
            var muxer = new MpegTsMuxer(new MediaTrackSet([AudioTrack()]));

            var es = ReadPes(ReadPackets(muxer.BuildSegment([new MediaPacket(1, 0, 0, true, 0, new byte[8184])])), VideoPid).Single().Es;

            es.Should().HaveCount(8191);
            es.Take(7).Should().Equal(AdtsHeader(1, 4, 1, 8184));
        }

        // ---- Validation ----

        public static TheoryData<string, MediaPacket[]> InvalidPackets => new()
        {
            { "unknown track", [new MediaPacket(9, 0, 0, true, 0, new byte[] { 1 })] },
            { "config version mismatch", [new MediaPacket(1, 0, 0, true, 1, new byte[] { 1 })] },
            { "negative DTS", [Audio(-1)] },
            { "PTS before DTS", [Video(40, key: true, pts: 0)] },
            { "DTS not increasing", [Audio(0), Audio(0)] },
            { "DTS going back", [Audio(10), Audio(5)] },
            { "zero-length NAL", [Video(0, key: true, new byte[] { 0, 0, 0, 0 })] },
            { "NAL past the end", [Video(0, key: true, new byte[] { 0, 0, 0, 9, 0x65 })] },
            { "truncated prefix", [Video(0, key: true, new byte[] { 0, 0 })] },
            { "empty video payload", [Video(0, key: true, Array.Empty<byte>())] },
            { "AAC payload too long for ADTS", [new MediaPacket(1, 0, 0, true, 0, new byte[8185])] },
            { "timestamp overflowing a 90 kHz clock", [Audio(long.MaxValue / 10)] },
            { "valid packets before a bad one", [Video(0, key: true), Audio(0), Audio(-5)] }
        };

        [Theory]
        [MemberData(nameof(InvalidPackets))]
        public void BuildSegment_InvalidPackets_Should_Throw_And_Leave_The_Muxer_Unchanged(string reason, MediaPacket[] packets)
        {
            var muxer = new MpegTsMuxer(new MediaTrackSet([VideoTrack(), AudioTrack()]));
            var reference = new MpegTsMuxer(new MediaTrackSet([VideoTrack(), AudioTrack()]));
            muxer.BuildSegment([Video(0, key: true), Audio(0)]);
            reference.BuildSegment([Video(0, key: true), Audio(0)]);

            var act = () => muxer.BuildSegment(packets.Select(p => p with { Dts = p.Dts + (p.Dts >= 0 ? 100 : 0), Pts = p.Pts + (p.Dts >= 0 ? 100 : 0) }).ToList());

            act.Should().Throw<ArgumentException>(reason).Which.ParamName.Should().Be("packets");
            muxer.BuildSegment([Video(200, key: true), Audio(200)]).Should().Equal(reference.BuildSegment([Video(200, key: true), Audio(200)]));
        }

        [Fact]
        public void BuildSegment_DtsNotAfterThePreviousSegment_Should_Throw()
        {
            var muxer = new MpegTsMuxer(new MediaTrackSet([AudioTrack()]));
            muxer.BuildSegment([Audio(100)]);

            var act = () => muxer.BuildSegment([Audio(100)]);

            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void BuildSegment_MalformedVideoPayload_Should_Wrap_The_InvalidDataException()
        {
            var muxer = new MpegTsMuxer(new MediaTrackSet([VideoTrack()]));

            var act = () => muxer.BuildSegment([Video(0, key: true, new byte[] { 0, 0, 0, 5, 0x65 })]);

            act.Should().ThrowExactly<ArgumentException>().WithInnerException<InvalidDataException>();
        }

        [Fact]
        public void BuildSegment_NullPackets_Should_Throw()
        {
            var muxer = new MpegTsMuxer(new MediaTrackSet([AudioTrack()]));

            var act = () => muxer.BuildSegment((IReadOnlyList<MediaPacket>)null!);

            act.Should().ThrowExactly<ArgumentNullException>();
        }

        [Fact]
        public void BuildSegment_EmptyPackets_Should_Throw()
        {
            var muxer = new MpegTsMuxer(new MediaTrackSet([AudioTrack()]));

            var act = () => muxer.BuildSegment(new List<MediaPacket>());

            act.Should().ThrowExactly<ArgumentException>();
        }

        [Fact]
        public void BuildSegment_PacketsOnFrameModeMuxer_Should_Throw()
        {
            var muxer = new MpegTsMuxer(1, 44100);

            var act = () => muxer.BuildSegment([Audio(0)]);

            act.Should().ThrowExactly<InvalidOperationException>();
        }

        [Fact]
        public void BuildSegment_FramesOnPacketModeMuxer_Should_Throw()
        {
            var muxer = new MpegTsMuxer(new MediaTrackSet([AudioTrack()]));

            var act = () => muxer.BuildSegment([new Mp4EncodedFrame([1], 1024)]);

            act.Should().ThrowExactly<InvalidOperationException>();
        }

        [Fact]
        public void Constructor_Null_Should_Throw()
        {
            var act = () => new MpegTsMuxer(null!);

            act.Should().ThrowExactly<ArgumentNullException>();
        }

        [Fact]
        public void Constructor_EmptyTrackSet_Should_Throw()
        {
            var act = () => new MpegTsMuxer(new MediaTrackSet([]));

            act.Should().ThrowExactly<ArgumentException>();
        }

        [Theory]
        [InlineData(MediaCodec.Mp3)]
        [InlineData(MediaCodec.Opus)]
        [InlineData(MediaCodec.Av1)]
        [InlineData(MediaCodec.Vp9)]
        public void Constructor_UnsupportedCodec_Should_Throw(MediaCodec codec)
        {
            var track = new MediaTrackInfo(2, MediaKind.Audio, codec, 1000, new byte[] { 1 }, 0);

            var act = () => new MpegTsMuxer(new MediaTrackSet([AudioTrack(), track]));

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Theory]
        [InlineData("2a08", "HE-AAC (object type 5)")]
        [InlineData("f80820", "escaped object type 32")]
        [InlineData("1780562208", "explicit 44100 Hz sample rate")]
        [InlineData("1200", "channel configuration 0 (PCE)")]
        [InlineData("1240", "channel configuration 8")]
        public void Constructor_AacTrackAdtsCannotCarry_Should_Throw_NotSupported(string config, string reason)
        {
            var track = new MediaTrackInfo(0, MediaKind.Audio, MediaCodec.Aac, 1000, Convert.FromHexString(config), 0);

            var act = () => new MpegTsMuxer(new MediaTrackSet([track]));

            act.Should().ThrowExactly<NotSupportedException>(reason);
        }

        public static TheoryData<string, MediaTrackInfo> InvalidTracks => new()
        {
            { "AAC without config", new MediaTrackInfo(0, MediaKind.Audio, MediaCodec.Aac, 1000, ReadOnlyMemory<byte>.Empty, 0) },
            { "truncated AudioSpecificConfig", new MediaTrackInfo(0, MediaKind.Audio, MediaCodec.Aac, 1000, new byte[] { 0x12 }, 0) },
            { "reserved frequency index", new MediaTrackInfo(0, MediaKind.Audio, MediaCodec.Aac, 1000, Convert.FromHexString("1688"), 0) },
            { "invalid avcC", new MediaTrackInfo(0, MediaKind.Video, MediaCodec.H264, 1000, new byte[] { 1, 2 }, 0) },
            { "invalid hvcC", new MediaTrackInfo(0, MediaKind.Video, MediaCodec.H265, 1000, new byte[] { 1, 2 }, 0) }
        };

        [Theory]
        [MemberData(nameof(InvalidTracks))]
        public void Constructor_InvalidTrack_Should_Throw(string reason, MediaTrackInfo track)
        {
            var act = () => new MpegTsMuxer(new MediaTrackSet([track]));

            act.Should().ThrowExactly<ArgumentException>(reason).Which.ParamName.Should().Be("tracks");
        }

        [Theory]
        [InlineData(17, 0)]
        [InlineData(0, 33)]
        public void Constructor_TooManyTracksForTheStreamIds_Should_Throw(int videoTracks, int audioTracks)
        {
            var tracks = Enumerable.Range(0, videoTracks).Select(VideoTrack)
                .Concat(Enumerable.Range(videoTracks, audioTracks).Select(id => AudioTrack() with { TrackId = id }))
                .ToList();

            var act = () => new MpegTsMuxer(new MediaTrackSet(tracks));

            act.Should().ThrowExactly<ArgumentException>();
        }

        [Fact]
        public void Constructor_MaximumTracks_Should_Number_Pids_And_Stream_Ids_In_Order()
        {
            var tracks = Enumerable.Range(0, 16).Select(VideoTrack)
                .Concat(Enumerable.Range(16, 32).Select(id => AudioTrack() with { TrackId = id }))
                .ToList();
            var muxer = new MpegTsMuxer(new MediaTrackSet(tracks));

            var ts = ReadPackets(muxer.BuildSegment([new MediaPacket(15, 0, 0, true, 0, LengthPrefixed([0x65])), new MediaPacket(47, 0, 0, true, 0, new byte[] { 0x21 })]));

            // 48 streams make the PMT span two packets.
            ts.Count(p => p.Pid == 0x1000).Should().Be(2);
            ReadPes(ts, 0x100 + 15).Single().StreamId.Should().Be(0xEF);
            ReadPes(ts, 0x100 + 47).Single().StreamId.Should().Be(0xDF);
        }

        // Env-gated cross-check: set EGGENCODER_FFMPEG_ORACLE=1 with ffmpeg/ffprobe on PATH to run
        // it; passes trivially otherwise, so CI needs no ffmpeg.
        [Theory]
        [InlineData("h264")]
        [InlineData("hevc")]
        public void Ffmpeg_OnConcatenatedSegments_Should_Report_And_Decode_Every_Stream(string fixture)
        {
            if (Environment.GetEnvironmentVariable("EGGENCODER_FFMPEG_ORACLE") != "1")
            {
                return;
            }

            var capture = fixture == "h264" ? H264AacCapture() : FlvDemuxerTest.Demux(File.ReadAllBytes(FlvDemuxerTest.HevcFixture));
            var muxer = new MpegTsMuxer(capture.Demuxer.Tracks!);
            var cut = capture.Packets.Where(p => p.IsKeyframe && p.TrackId == FlvDemuxer.VideoTrackId).Select(p => p.Dts).Skip(1).FirstOrDefault(long.MaxValue);
            var bytes = new List<byte>(muxer.BuildSegment(capture.Packets.Where(p => p.Dts < cut).ToList()));
            if (cut != long.MaxValue)
            {
                bytes.AddRange(muxer.BuildSegment(capture.Packets.Where(p => p.Dts >= cut).ToList()));
            }

            var path = Path.Combine(Path.GetTempPath(), $"eggencoder-ts-{Guid.NewGuid():N}.ts");
            try
            {
                File.WriteAllBytes(path, bytes.ToArray());

                var (probeExit, probeOutput, probeErrors) = Run("ffprobe", $"-v error -show_entries stream=codec_name,width,height,sample_rate,channels -show_entries packet=stream_index -of compact=p=0 \"{path}\"");
                probeExit.Should().Be(0);
                probeErrors.Should().BeEmpty();
                if (fixture == "h264")
                {
                    probeOutput.Should().Contain("codec_name=h264|width=64|height=64");
                    probeOutput.Should().Contain("codec_name=aac|sample_rate=44100|channels=1");
                }
                else
                {
                    probeOutput.Should().Contain("codec_name=hevc");
                }

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

        private static MediaTrackInfo VideoTrack(int trackId = 0) =>
            new(trackId, MediaKind.Video, MediaCodec.H264, 1000, FlvTestBuilder.AvcC, 0, width: 64, height: 64);

        private static MediaTrackInfo AudioTrack() =>
            new(1, MediaKind.Audio, MediaCodec.Aac, 1000, FlvTestBuilder.AacConfig, 0, 44100, 1);

        private static MediaPacket Video(long dts, bool key, byte[]? payload = null, long? pts = null) =>
            new(0, pts ?? dts, dts, key, 0, payload ?? LengthPrefixed([(byte)(key ? 0x65 : 0x41), 0x80]));

        private static MediaPacket Audio(long dts) => new(1, dts, dts, true, 0, new byte[] { 0x21, 0x10 });

        private static byte[] SingleVideoEs(byte[] segment) => ReadPes(ReadPackets(segment), VideoPid).Single().Es;

        private static byte[] ExpectedH264Es(MediaPacket packet)
        {
            var record = AvcDecoderConfigurationRecord.Parse(FlvTestBuilder.AvcC);
            var expected = new List<byte>(H264Aud);
            if (packet.IsKeyframe && !Nals(packet.Payload.ToArray()).Any(n => (n[0] & 0x1F) == 7))
            {
                expected.AddRange(AnnexB(record.SequenceParameterSets[0].ToArray()));
                expected.AddRange(AnnexB(record.PictureParameterSets[0].ToArray()));
            }

            expected.AddRange(AnnexBConverter.ToAnnexB(packet.Payload.Span));
            return expected.ToArray();
        }

        private static byte[] AnnexB(byte[] nal) => [0, 0, 0, 1, .. nal];

        private static byte[] LengthPrefixed(params byte[][] nals)
        {
            var result = new List<byte>();
            foreach (var nal in nals)
            {
                result.AddRange([(byte)(nal.Length >> 24), (byte)(nal.Length >> 16), (byte)(nal.Length >> 8), (byte)nal.Length]);
                result.AddRange(nal);
            }

            return result.ToArray();
        }

        private static List<byte[]> Nals(byte[] lengthPrefixed)
        {
            var nals = new List<byte[]>();
            for (var position = 0; position < lengthPrefixed.Length;)
            {
                var length = (lengthPrefixed[position] << 24) | (lengthPrefixed[position + 1] << 16) | (lengthPrefixed[position + 2] << 8) | lengthPrefixed[position + 3];
                nals.Add(lengthPrefixed[(position + 4)..(position + 4 + length)]);
                position += 4 + length;
            }

            return nals;
        }

        // ISO/IEC 14496-3 1.A.2.2, built field by field here rather than through AdtsFrameWriter.
        private static byte[] AdtsHeader(int profile, int frequencyIndex, int channels, int payloadLength)
        {
            var bits = new System.Text.StringBuilder();
            void Add(int value, int width) => bits.Append(Convert.ToString(value, 2).PadLeft(width, '0'));
            Add(0xFFF, 12);
            Add(0, 1);
            Add(0, 2);
            Add(1, 1);
            Add(profile, 2);
            Add(frequencyIndex, 4);
            Add(0, 1);
            Add(channels, 3);
            Add(0, 4);
            Add(7 + payloadLength, 13);
            Add(0x7FF, 11);
            Add(0, 2);
            return Enumerable.Range(0, 7).Select(i => Convert.ToByte(bits.ToString(i * 8, 8), 2)).ToArray();
        }
    }
}
