using EggEncoder.Transform;

namespace EggEncoder.Codecs.Aac
{
    // ADTS (Audio Data Transport Stream) frame-header writer -- the fixed 7-byte header a raw AAC
    // access unit needs when carried as a standalone ADTS/.aac file or as MPEG-TS payload. Extracted
    // out of AacFrameEncoder (which still uses it for its own existing ADTS-to-file output path) so
    // this framing logic has exactly one implementation; the MPEG-TS muxer planned in
    // docs/streaming-plan.md item 4 reuses this same helper rather than a second copy.
    internal static class AdtsFrameWriter
    {
        private const uint AdtsSyncWord = 0xFFF;
        private const uint AacLcProfile = 1;
        private const uint NoCrcProtectionAbsent = 1;
        private const int AdtsHeaderByteLength = 7;
        private const uint AdtsBufferFullness = 0x7FF;

        public static byte[] Wrap(byte[] payload, int sampleRateIndex, int channels)
        {
            var writer = new BitWriter();

            writer.WriteBits(AdtsSyncWord, 12);
            writer.WriteBits(0, 1);
            writer.WriteBits(0, 2);
            writer.WriteBits(NoCrcProtectionAbsent, 1);
            writer.WriteBits(AacLcProfile, 2);
            writer.WriteBits((uint)sampleRateIndex, 4);
            writer.WriteBits(0, 1);
            writer.WriteBits((uint)channels, 3);
            writer.WriteBits(0, 1);
            writer.WriteBits(0, 1);
            writer.WriteBits(0, 1);
            writer.WriteBits(0, 1);
            writer.WriteBits((uint)(AdtsHeaderByteLength + payload.Length), 13);
            writer.WriteBits(AdtsBufferFullness, 11);
            writer.WriteBits(0, 2);

            var header = writer.ToArray();
            var frame = new byte[header.Length + payload.Length];
            Buffer.BlockCopy(header, 0, frame, 0, header.Length);
            Buffer.BlockCopy(payload, 0, frame, header.Length, payload.Length);

            return frame;
        }
    }
}
