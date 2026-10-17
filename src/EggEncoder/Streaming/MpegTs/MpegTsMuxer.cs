using EggEncoder.Codecs.Aac;
using EggEncoder.Streaming.Mp4;

namespace EggEncoder.Streaming.MpegTs
{
    /// <summary>
    /// Builds MPEG-TS segments (ISO/IEC 13818-1) for a single mono AAC-LC track -- the alternative,
    /// ADTS-framed segment format to <see cref="FmpMediaSegmentWriter"/>'s own fMP4/CMAF one. Each
    /// call to <see cref="BuildSegment"/> produces one complete, independently-parseable <c>.ts</c>
    /// file (PAT + PMT repeated at the start of every segment, then one PES packet per frame) --
    /// matching real HLS TS segmenters' own convention that every segment is a standalone file with
    /// no cross-segment state, so PAT/PMT/continuity counters all restart at the beginning of each
    /// call. PTS continuity across segments is still preserved: this muxer tracks the running total
    /// sample count internally, the same way <see cref="FmpMediaSegmentWriter"/> tracks
    /// <c>baseMediaDecodeTime</c> -- build a fresh instance per rendition. See
    /// docs/streaming-plan.md item 4.
    /// </summary>
    public sealed class MpegTsMuxer
    {
        private const int PatPid = 0x0000;
        private const int PmtPid = 0x1000;
        private const int AudioPid = 0x0100;
        private const int TsPayloadSize = 184;
        private const byte AdtsAacStreamType = 0x0F;
        private const byte AudioStreamId = 0xC0;
        private const long PtsClockHz = 90000;
        private const long PtsWrapMask = (1L << 33) - 1;

        private readonly int _channels;
        private readonly int _sampleRate;
        private readonly int _sampleRateIndex;

        private long _cumulativeSampleCount;

        /// <exception cref="NotSupportedException"><paramref name="channels"/> is not 1, or <paramref name="sampleRate"/> is not a valid MPEG-4 AAC sample rate.</exception>
        public MpegTsMuxer(int channels, int sampleRate)
        {
            _sampleRateIndex = AacFrameEncoder.ValidateAndGetSampleRateIndex(channels, sampleRate);
            _channels = channels;
            _sampleRate = sampleRate;
        }

        /// <exception cref="ArgumentException"><paramref name="frames"/> is empty.</exception>
        public byte[] BuildSegment(IReadOnlyList<Mp4EncodedFrame> frames)
        {
            ArgumentNullException.ThrowIfNull(frames);
            if (frames.Count == 0)
            {
                throw new ArgumentException("A media segment must contain at least one frame.", nameof(frames));
            }

            var output = new List<byte>();

            var patContinuityCounter = 0;
            WritePayload(output, PatPid, BuildPatPayload(), ref patContinuityCounter);

            var pmtContinuityCounter = 0;
            WritePayload(output, PmtPid, BuildPmtPayload(), ref pmtContinuityCounter);

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

        private static byte[] BuildPmtPayload()
        {
            var section = new List<byte>
            {
                0x02, // table_id (PMT/TS_program_map_section)
            };

            const int streamLoopLength = 1 + 2 + 2; // stream_type + elementary_PID + ES_info_length
            const int sectionLength = 2 + 1 + 1 + 1 + 2 + 2 + streamLoopLength + 4;
            section.Add((byte)(0xB0 | ((sectionLength >> 8) & 0x0F)));
            section.Add((byte)(sectionLength & 0xFF));

            section.Add(0x00); // program_number
            section.Add(0x01);
            section.Add(0xC1);
            section.Add(0x00); // section_number
            section.Add(0x00); // last_section_number

            // PCR_PID points at the audio elementary stream itself (no separate PCR-only stream),
            // matching real single-audio-track convention -- this muxer does not insert adaptation-
            // field PCR values at all (a deliberate MVP scope limitation; see docs/streaming-plan.md
            // item 4's own status note).
            section.Add((byte)(0xE0 | ((AudioPid >> 8) & 0x1F)));
            section.Add((byte)(AudioPid & 0xFF));
            section.Add(0xF0); // reserved(4) + program_info_length(12)=0
            section.Add(0x00);

            section.Add(AdtsAacStreamType);
            section.Add((byte)(0xE0 | ((AudioPid >> 8) & 0x1F)));
            section.Add((byte)(AudioPid & 0xFF));
            section.Add(0xF0); // reserved(4) + ES_info_length(12)=0
            section.Add(0x00);

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

            EncodePts(pts33Bit, packet.AsSpan(9, 5));

            adtsFrame.CopyTo(packet, 9 + 5);

            return packet;
        }

        // The 5-byte PTS-only timestamp format (ISO/IEC 13818-1 §2.4.3.6): '0010' PTS[32:30] '1'
        // PTS[29:15] '1' PTS[14:0] '1' -- confirmed bit-for-bit against a real ffmpeg-produced .ts
        // fixture's own PES header.
        private static void EncodePts(long pts33Bit, Span<byte> destination)
        {
            var high3 = (pts33Bit >> 30) & 0x7;
            var mid15 = (pts33Bit >> 15) & 0x7FFF;
            var low15 = pts33Bit & 0x7FFF;

            destination[0] = (byte)(0x20 | (high3 << 1) | 0x01);

            var midField = (mid15 << 1) | 1;
            destination[1] = (byte)(midField >> 8);
            destination[2] = (byte)midField;

            var lowField = (low15 << 1) | 1;
            destination[3] = (byte)(lowField >> 8);
            destination[4] = (byte)lowField;
        }
    }
}
