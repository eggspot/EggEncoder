namespace EggEncoder.Codecs.WavPack
{
    // WavPack's own block-level integrity check over the decoded integer PCM samples of a block, in
    // decode order -- confirmed empirically against real ffmpeg-produced WavPack blocks (not a
    // generic CRC-32: it's a simple multiply-accumulate running checksum, seeded to all-ones).
    internal static class WavPackCrc
    {
        public const uint Seed = 0xFFFFFFFF;

        public static uint Append(uint crc, int sample) => (crc * 3) + (uint)sample;
    }
}
