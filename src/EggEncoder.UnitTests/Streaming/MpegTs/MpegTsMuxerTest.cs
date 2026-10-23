using EggEncoder.Streaming.Mp4;
using EggEncoder.Streaming.MpegTs;
using FluentAssertions;

namespace EggEncoder.UnitTests.Streaming.MpegTs
{
    public class MpegTsMuxerTest
    {
        private const int Channels = 1;
        private const int SampleRate = 44100;
        private const int TsPacketSize = 188;

        [Fact]
        public void Constructor_WithStereoChannels_Should_Throw()
        {
            var act = () => new MpegTsMuxer(2, SampleRate);

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void BuildSegment_WithNoFrames_Should_Throw()
        {
            var muxer = new MpegTsMuxer(Channels, SampleRate);

            var act = () => muxer.BuildSegment(new List<Mp4EncodedFrame>());

            act.Should().ThrowExactly<ArgumentException>();
        }

        [Fact]
        public void BuildSegment_Should_ProducePacketsThatAreAllExactly188Bytes()
        {
            var muxer = new MpegTsMuxer(Channels, SampleRate);
            var segment = muxer.BuildSegment(BuildFrames([16, 23, 30]));

            (segment.Length % TsPacketSize).Should().Be(0);
        }

        [Fact]
        public void BuildSegment_Pat_And_Pmt_Crc32_Should_MatchIndependentlyComputedReferenceValues()
        {
            // Reference CRC-32/MPEG-2 values for this exact section content (table_id through the
            // program/stream loop, for this muxer's own chosen transport_stream_id=1/program_number=1/
            // PMT_PID=0x1000/PCR_PID=elementary_PID=0x0100), independently cross-checked against a
            // real ffmpeg-produced .ts fixture that happens to choose the exact same values -- not
            // just re-deriving the same algorithm this test is meant to verify.
            const uint expectedPatCrc = 0x2ab104b2;
            const uint expectedPmtCrc = 0xb69bc0d9;

            var muxer = new MpegTsMuxer(Channels, SampleRate);
            var segment = muxer.BuildSegment(BuildFrames([16]));

            var patPacket = segment.AsSpan(0, TsPacketSize);
            var pmtPacket = segment.AsSpan(TsPacketSize, TsPacketSize);

            ReadCrc32(patPacket, sectionLength: 13).Should().Be(expectedPatCrc);
            ReadCrc32(pmtPacket, sectionLength: 18).Should().Be(expectedPmtCrc);
        }

        [Fact]
        public void BuildSegment_Should_StartWithWellFormedPatThenPmt()
        {
            var muxer = new MpegTsMuxer(Channels, SampleRate);
            var segment = muxer.BuildSegment(BuildFrames([16]));

            var firstPacket = segment.AsSpan(0, TsPacketSize);
            var secondPacket = segment.AsSpan(TsPacketSize, TsPacketSize);

            firstPacket[0].Should().Be(0x47, "every TS packet starts with the sync byte");
            ReadPid(firstPacket).Should().Be(0x0000, "the first packet must be the PAT, at the well-known PID 0");
            firstPacket[5].Should().Be(0x00, "PAT table_id");

            secondPacket[0].Should().Be(0x47);
            var pmtPid = ParsePatForPmtPid(firstPacket);
            ReadPid(secondPacket).Should().Be(pmtPid, "the second packet must be the PMT, at the PID the PAT just declared");
            secondPacket[5].Should().Be(0x02, "PMT table_id");
        }

        [Fact]
        public void BuildSegment_Pmt_Should_DeclareAdtsAacStreamType()
        {
            var muxer = new MpegTsMuxer(Channels, SampleRate);
            var segment = muxer.BuildSegment(BuildFrames([16]));

            var pmtPacket = segment.AsSpan(TsPacketSize, TsPacketSize);

            ParsePmtStreamType(pmtPacket).Should().Be(0x0F, "ISO/IEC 13818-7 ADTS AAC");
        }

        [Fact]
        public void BuildSegment_Should_RoundTrip_FramePayloads_ByteForByte()
        {
            var muxer = new MpegTsMuxer(Channels, SampleRate);
            var frames = BuildFrames([16, 23, 30, 8]);

            var segment = muxer.BuildSegment(frames);
            var parsed = ParseAudioPesPackets(segment);

            parsed.Should().HaveCount(frames.Count);
            for (var i = 0; i < frames.Count; i++)
            {
                // Each parsed unit is the ADTS-wrapped frame (7-byte header + payload); strip the
                // header to compare against the original raw access unit this test built.
                parsed[i].AdtsFrame.Skip(7).Should().Equal(frames[i].Payload);
            }
        }

        [Fact]
        public void BuildSegment_Pts_Should_TrackCumulativeSampleCount_WithNoUnboundedDrift()
        {
            var muxer = new MpegTsMuxer(Channels, SampleRate);
            var frames = BuildFrames(Enumerable.Repeat(1024, 20).ToArray());

            var segment = muxer.BuildSegment(frames);
            var parsed = ParseAudioPesPackets(segment);

            var cumulativeSamples = 0L;
            foreach (var (_, pts) in parsed)
            {
                var expectedPts = (long)Math.Round(cumulativeSamples * 90000.0 / SampleRate);
                pts.Should().Be(expectedPts);
                cumulativeSamples += 1024;
            }
        }

        [Fact]
        public void BuildSegment_CalledTwice_Should_ContinuePtsFromPreviousSegment_ButRestartPatPmtContinuityCounters()
        {
            var muxer = new MpegTsMuxer(Channels, SampleRate);

            var first = muxer.BuildSegment(BuildFrames([1024, 1024]));
            var second = muxer.BuildSegment(BuildFrames([1024]));

            var firstPts = ParseAudioPesPackets(first).Select(p => p.Pts).ToList();
            var secondPts = ParseAudioPesPackets(second).Select(p => p.Pts).ToList();

            secondPts[0].Should().Be((long)Math.Round(2048 * 90000.0 / SampleRate), "PTS must continue from the first segment's own total sample count, not restart at 0");

            // Every segment is an independently-parseable file -- PAT/PMT/audio continuity counters
            // (the low 4 bits of every packet's 4th header byte) both restart at 0.
            (first[3] & 0x0F).Should().Be(0);
            (second[3] & 0x0F).Should().Be(0);
        }

        private static List<Mp4EncodedFrame> BuildFrames(int[] sampleCounts)
        {
            var frames = new List<Mp4EncodedFrame>();
            var seed = 1;
            foreach (var sampleCount in sampleCounts)
            {
                var payload = new byte[300 + seed]; // large enough that some frames span multiple TS packets
                for (var i = 0; i < payload.Length; i++)
                {
                    payload[i] = (byte)((seed + i) % 256);
                }

                frames.Add(new Mp4EncodedFrame(payload, sampleCount));
                seed += 13;
            }

            return frames;
        }

        private static int ReadPid(ReadOnlySpan<byte> packet) => ((packet[1] & 0x1F) << 8) | packet[2];

        // CRC32 is always the last 4 bytes of a PSI section; section_length counts every byte
        // after the section_length field itself (including the CRC), so it lands at
        // pointer_field(1) + table_id(1) + section_length_field(2) + (sectionLength - 4) bytes in.
        private static uint ReadCrc32(ReadOnlySpan<byte> packet, int sectionLength)
        {
            var offset = 4 + sectionLength;
            return (uint)((packet[offset] << 24) | (packet[offset + 1] << 16) | (packet[offset + 2] << 8) | packet[offset + 3]);
        }

        private static int ParsePatForPmtPid(ReadOnlySpan<byte> patPacket)
        {
            // pointer_field(1) + table_id(1) + section_length(2) + transport_stream_id(2) +
            // reserved/version/cn(1) + section_number(1) + last_section_number(1) + program_number(2) = offset 11
            var offset = 4 + 11;
            return ((patPacket[offset] & 0x1F) << 8) | patPacket[offset + 1];
        }

        private static byte ParsePmtStreamType(ReadOnlySpan<byte> pmtPacket)
        {
            // pointer_field(1) + table_id(1) + section_length(2) + program_number(2) +
            // reserved/version/cn(1) + section_number(1) + last_section_number(1) + PCR_PID(2) +
            // program_info_length(2) = offset 13 -- the first byte of the (single) stream loop entry.
            var offset = 4 + 13;
            return pmtPacket[offset];
        }

        // Reassembles PES packets carried on PID 0x0100 (this muxer's own fixed audio PID),
        // returning each one's ADTS-wrapped frame bytes and decoded PTS. Not a general-purpose
        // MPEG-TS parser -- just enough to verify this muxer's own output, mirroring
        // FmpSegmentWriterTest's own "self-parse, don't trust a general reader" approach for
        // fragmented MP4.
        private static List<(byte[] AdtsFrame, long Pts)> ParseAudioPesPackets(byte[] tsBytes)
        {
            const int audioPid = 0x0100;

            var results = new List<(byte[] AdtsFrame, long Pts)>();
            byte[]? buffer = null;
            var expectedTotalLength = -1;

            for (var offset = 0; offset + TsPacketSize <= tsBytes.Length; offset += TsPacketSize)
            {
                var packet = tsBytes.AsSpan(offset, TsPacketSize);
                if (ReadPid(packet) != audioPid)
                {
                    continue;
                }

                var payloadUnitStart = (packet[1] & 0x40) != 0;
                var payload = packet.Slice(4, TsPacketSize - 4).ToArray();

                if (payloadUnitStart)
                {
                    buffer = payload;
                    expectedTotalLength = 6 + ((buffer[4] << 8) | buffer[5]);
                }
                else if (buffer is not null)
                {
                    var combined = new byte[buffer.Length + payload.Length];
                    buffer.CopyTo(combined, 0);
                    payload.CopyTo(combined, buffer.Length);
                    buffer = combined;
                }

                if (buffer is not null && expectedTotalLength > 0 && buffer.Length >= expectedTotalLength)
                {
                    var pesBytes = buffer[..expectedTotalLength];
                    var pts = DecodePts(pesBytes.AsSpan(9, 5));
                    var adts = pesBytes[14..];
                    results.Add((adts, pts));
                    buffer = null;
                    expectedTotalLength = -1;
                }
            }

            return results;
        }

        private static long DecodePts(ReadOnlySpan<byte> ptsBytes)
        {
            var high3 = (ptsBytes[0] >> 1) & 0x7;
            var mid15 = (((ptsBytes[1] << 8) | ptsBytes[2]) >> 1) & 0x7FFF;
            var low15 = (((ptsBytes[3] << 8) | ptsBytes[4]) >> 1) & 0x7FFF;

            return ((long)high3 << 30) | ((long)mid15 << 15) | (long)low15;
        }
    }
}
