using EggEncoder.Codecs.Aac;
using EggEncoder.Media;
using EggEncoder.Streaming.Mp4;

namespace EggEncoder.Streaming.MpegTs
{
    /// <summary>
    /// Builds MPEG-TS segments (ISO/IEC 13818-1) -- the alternative, ADTS-framed segment format to
    /// <see cref="FmpMediaSegmentWriter"/>'s own fMP4/CMAF one. Two modes, chosen by constructor: a
    /// single mono AAC-LC track fed <see cref="Mp4EncodedFrame"/>s from this project's own encoders
    /// (docs/streaming-plan.md item 4), and a <see cref="MediaTrackSet"/> of H.264, H.265 and AAC
    /// tracks fed <see cref="MediaPacket"/>s (docs/live-ingest-restream-plan.md item 5b). Each
    /// <c>BuildSegment</c> call produces one complete, independently-parseable <c>.ts</c> file
    /// (PAT + PMT at the start of every segment, then one PES packet per frame or packet). In frame
    /// mode PAT/PMT/continuity counters all restart at the beginning of each call; in packet mode
    /// they continue across calls, so segments read back to back form one continuous stream, as
    /// HLS players read them. Timestamps carry forward from one call to the next in both modes --
    /// build a fresh instance per rendition.
    /// </summary>
    public sealed partial class MpegTsMuxer
    {
        private const int PatPid = 0x0000;
        private const int PmtPid = 0x1000;
        private const int AudioPid = 0x0100;
        private const int TsPayloadSize = 184;
        private const byte AdtsAacStreamType = 0x0F;
        private const byte AudioStreamId = 0xC0;
        private const long PtsClockHz = 90000;
        private const long PtsWrapMask = (1L << 33) - 1;
        private const int PtsOnlyPrefix = 0x2;

        private readonly int _channels;
        private readonly int _sampleRate;
        private readonly int _sampleRateIndex;

        private long _cumulativeSampleCount;

        /// <summary>Frame mode: one mono AAC-LC track, fed by <see cref="BuildSegment(IReadOnlyList{Mp4EncodedFrame})"/>.</summary>
        /// <exception cref="NotSupportedException"><paramref name="channels"/> is not 1, or <paramref name="sampleRate"/> is not a valid MPEG-4 AAC sample rate.</exception>
        public MpegTsMuxer(int channels, int sampleRate)
        {
            _sampleRateIndex = AacFrameEncoder.ValidateAndGetSampleRateIndex(channels, sampleRate);
            _channels = channels;
            _sampleRate = sampleRate;
        }

        /// <exception cref="ArgumentException"><paramref name="frames"/> is empty.</exception>
        /// <exception cref="InvalidOperationException">This muxer was built for packets (the <see cref="MediaTrackSet"/> constructor).</exception>
        public byte[] BuildSegment(IReadOnlyList<Mp4EncodedFrame> frames)
        {
            ArgumentNullException.ThrowIfNull(frames);
            if (_packetTracks is not null)
            {
                throw new InvalidOperationException("This muxer was built for a MediaTrackSet; pass MediaPackets instead of frames.");
            }

            if (frames.Count == 0)
            {
                throw new ArgumentException("A media segment must contain at least one frame.", nameof(frames));
            }

            var output = new List<byte>();

            var patContinuityCounter = 0;
            WritePayload(output, PatPid, BuildPatPayload(), ref patContinuityCounter);

            var pmtContinuityCounter = 0;
            WritePayload(output, PmtPid, BuildPmtPayload(AudioPid, [(AdtsAacStreamType, AudioPid)]), ref pmtContinuityCounter);

            var audioContinuityCounter = 0;
            foreach (var frame in frames)
            {
                var adtsFrame = AdtsFrameWriter.Wrap(frame.Payload, _sampleRateIndex, _channels);
                var pts = (long)Math.Round(_cumulativeSampleCount * (double)PtsClockHz / _sampleRate) & PtsWrapMask;

                WritePayload(output, AudioPid, BuildPesPacket(adtsFrame, pts), ref audioContinuityCounter);

                _cumulativeSampleCount += frame.SampleCount;
            }

            return output.ToArray();
        }

        // Splits one logical payload (a PSI section, including its own leading pointer_field, or a
        // complete PES packet) across as many 188-byte TS packets as it takes: payload_unit_start_
        // indicator set only on the first packet, adaptation_field_control always "payload only"
        // (01) -- confirmed against a real ffmpeg-produced .ts fixture that every PAT/PMT/PES packet
        // uses this same simple 0xFF-stuffed-payload convention, never an adaptation field, even
        // for a short final packet that doesn't fill the full 184-byte payload capacity.
        private static void WritePayload(List<byte> output, int pid, byte[] payload, ref int continuityCounter)
        {
            var offset = 0;
            do
            {
                var chunkSize = Math.Min(TsPayloadSize, payload.Length - offset);
                var payloadUnitStart = offset == 0;

                output.Add(0x47);
                output.Add((byte)((payloadUnitStart ? 0x40 : 0x00) | ((pid >> 8) & 0x1F)));
                output.Add((byte)(pid & 0xFF));
                output.Add((byte)(0x10 | (continuityCounter & 0x0F))); // adaptation_field_control=01 (payload only)

                for (var i = 0; i < chunkSize; i++)
                {
                    output.Add(payload[offset + i]);
                }

                for (var i = chunkSize; i < TsPayloadSize; i++)
                {
                    output.Add(0xFF); // stuffing -- the real encoder fixture this was verified against pads exactly this way
                }

                offset += chunkSize;
                continuityCounter = (continuityCounter + 1) & 0x0F;
            } while (offset < payload.Length);
        }

        private static byte[] BuildPatPayload()
        {
            var section = new List<byte>
            {
                0x00, // table_id (PAT)
            };

            const int sectionLength = 2 + 1 + 1 + 1 + 2 + 2 + 4; // transport_stream_id..program loop..CRC
            section.Add((byte)(0xB0 | ((sectionLength >> 8) & 0x0F)));
            section.Add((byte)(sectionLength & 0xFF));

            section.Add(0x00); // transport_stream_id
            section.Add(0x01);
            section.Add(0xC1); // reserved(2) + version_number(5)=0 + current_next_indicator(1)=1
            section.Add(0x00); // section_number
            section.Add(0x00); // last_section_number

            section.Add(0x00); // program_number
            section.Add(0x01);
            section.Add((byte)(0xE0 | ((PmtPid >> 8) & 0x1F))); // reserved(3) + PMT_PID high bits
            section.Add((byte)(PmtPid & 0xFF));

            AppendCrc32(section);

            return PrependPointerField(section);
        }

        // PCR_PID and one stream loop entry (no descriptors) per elementary stream. Frame mode
        // points PCR_PID at its single audio stream and writes no PCR at all (a deliberate MVP
        // scope limitation; see docs/streaming-plan.md item 4's own status note); packet mode
        // writes PCR on the PID it names here.
        private static byte[] BuildPmtPayload(int pcrPid, IReadOnlyList<(byte StreamType, int Pid)> streams)
        {
            var section = new List<byte>
            {
                0x02, // table_id (PMT/TS_program_map_section)
            };

            const int streamLoopEntryLength = 1 + 2 + 2; // stream_type + elementary_PID + ES_info_length
            var sectionLength = 2 + 1 + 1 + 1 + 2 + 2 + (streams.Count * streamLoopEntryLength) + 4;
            section.Add((byte)(0xB0 | ((sectionLength >> 8) & 0x0F)));
            section.Add((byte)(sectionLength & 0xFF));

            section.Add(0x00); // program_number
            section.Add(0x01);
            section.Add(0xC1);
            section.Add(0x00); // section_number
            section.Add(0x00); // last_section_number

            section.Add((byte)(0xE0 | ((pcrPid >> 8) & 0x1F)));
            section.Add((byte)(pcrPid & 0xFF));
            section.Add(0xF0); // reserved(4) + program_info_length(12)=0
            section.Add(0x00);

            foreach (var (streamType, pid) in streams)
            {
                section.Add(streamType);
                section.Add((byte)(0xE0 | ((pid >> 8) & 0x1F)));
                section.Add((byte)(pid & 0xFF));
                section.Add(0xF0); // reserved(4) + ES_info_length(12)=0
                section.Add(0x00);
            }

            AppendCrc32(section);

            return PrependPointerField(section);
        }

        private static void AppendCrc32(List<byte> section)
        {
            var crc = MpegTsCrc32.Compute(section.ToArray());
            section.Add((byte)(crc >> 24));
            section.Add((byte)(crc >> 16));
            section.Add((byte)(crc >> 8));
            section.Add((byte)crc);
        }

        private static byte[] PrependPointerField(List<byte> section)
        {
            var payload = new byte[section.Count + 1];
            payload[0] = 0x00; // pointer_field: the section starts immediately after this byte
            section.CopyTo(payload, 1);
            return payload;
        }

        private static byte[] BuildPesPacket(byte[] adtsFrame, long pts33Bit)
        {
            const int optionalHeaderLength = 1 + 1 + 1 + 5; // the two flag bytes + PES_header_data_length + the 5 PTS bytes
            var pesPacketLength = optionalHeaderLength + adtsFrame.Length;

            var packet = new byte[6 + optionalHeaderLength + adtsFrame.Length];
            packet[0] = 0x00;
            packet[1] = 0x00;
            packet[2] = 0x01;
            packet[3] = AudioStreamId;
            packet[4] = (byte)(pesPacketLength >> 8);
            packet[5] = (byte)pesPacketLength;
            packet[6] = 0x80; // '10' marker + scrambling/priority/alignment/copyright/original all 0
            packet[7] = 0x80; // PTS_DTS_flags='10' (PTS only) + ESCR/ES_rate/trick-mode/copy-info/CRC/extension all 0
            packet[8] = 0x05; // PES_header_data_length -- 5 bytes of PTS follow

            EncodeTimestamp(pts33Bit, PtsOnlyPrefix, packet.AsSpan(9, 5));

            adtsFrame.CopyTo(packet, 9 + 5);

            return packet;
        }

        // The 5-byte PTS/DTS timestamp format (ISO/IEC 13818-1 §2.4.3.6): a 4-bit prefix ('0010'
        // for a PTS alone, '0011' for a PTS followed by a DTS, '0001' for that DTS), PTS[32:30] '1'
        // PTS[29:15] '1' PTS[14:0] '1' -- the PTS-only form confirmed bit-for-bit against a real
        // ffmpeg-produced .ts fixture's own PES header.
        private static void EncodeTimestamp(long timestamp33Bit, int prefix, Span<byte> destination)
        {
            var high3 = (timestamp33Bit >> 30) & 0x7;
            var mid15 = (timestamp33Bit >> 15) & 0x7FFF;
            var low15 = timestamp33Bit & 0x7FFF;

            destination[0] = (byte)((prefix << 4) | (int)(high3 << 1) | 0x01);

            var midField = (mid15 << 1) | 1;
            destination[1] = (byte)(midField >> 8);
            destination[2] = (byte)midField;

            var lowField = (low15 << 1) | 1;
            destination[3] = (byte)(lowField >> 8);
            destination[4] = (byte)lowField;
        }
    }
}
