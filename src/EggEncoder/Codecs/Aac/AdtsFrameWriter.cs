namespace EggEncoder.Codecs.Aac
{
    // ADTS (Audio Data Transport Stream) frame-header writer -- the fixed 7-byte header a raw AAC
    // access unit needs when carried as a standalone ADTS/.aac file or as MPEG-TS payload. Extracted
    // out of AacFrameEncoder (which still uses it for its own existing ADTS-to-file output path) so
    // this framing logic has exactly one implementation; the MPEG-TS muxer planned in
    // docs/streaming-plan.md item 4 reuses this same helper rather than a second copy.
    internal static class AdtsFrameWriter
    {
        public const int HeaderLength = 7;

        // frame_length is 13 bits and counts the header too.
        public const int MaxPayloadLength = (1 << 13) - 1 - HeaderLength;

        private const int AacLcProfile = 1;

        public static byte[] Wrap(byte[] payload, int sampleRateIndex, int channels)
        {
            var frame = new byte[HeaderLength + payload.Length];
            WriteHeader(frame, AacLcProfile, sampleRateIndex, channels, payload.Length);
            payload.CopyTo(frame, HeaderLength);

            return frame;
        }

        // ISO/IEC 14496-3 1.A.2.2 adts_fixed_header + adts_variable_header, no CRC: syncword 0xFFF,
        // ID 0 (MPEG-4), layer 0, protection_absent 1, profile (audioObjectType - 1), the sampling
        // frequency index, private_bit 0, channel_configuration, four zero flag bits, frame_length
        // (header + payload), adts_buffer_fullness 0x7FF (variable rate) and one raw data block.
        public static void WriteHeader(Span<byte> destination, int profile, int sampleRateIndex, int channels, int payloadLength)
        {
            const int bufferFullness = 0x7FF;
            var frameLength = HeaderLength + payloadLength;

            destination[0] = 0xFF;
            destination[1] = 0xF1;
            destination[2] = (byte)((profile << 6) | (sampleRateIndex << 2) | ((channels >> 2) & 0x1));
            destination[3] = (byte)(((channels & 0x3) << 6) | ((frameLength >> 11) & 0x3));
            destination[4] = (byte)(frameLength >> 3);
            destination[5] = (byte)(((frameLength & 0x7) << 5) | (bufferFullness >> 6));
            destination[6] = (byte)((bufferFullness & 0x3F) << 2);
        }
    }
}
