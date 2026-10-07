namespace EggEncoder.UnitTests.TestUtilities
{
    // Hand-builds minimal/malformed Yamaha ADPCM (format tag 32) WAV files for WavReader's own
    // validation-path tests. Unlike WavImaAdpcmFileBuilder/WavMsAdpcmFileBuilder, Yamaha ADPCM has no
    // block structure at all -- no cbSize/wSamplesPerBlock extension, no per-channel block header --
    // so this builder is correspondingly simpler: just the plain 16-byte fmt fields plus a 2-byte
    // cbSize of 0, an optional 'fact' chunk, and raw all-zero nibble data (decodes to a flat, silent
    // stream, which is all these validation-focused fixtures need).
    public static class WavYamahaAdpcmFileBuilder
    {
        private const int YamahaAdpcmFormatTag = 32;

        public static void CreateMinimal(string filePath, int channels, int sampleRate, long? totalSamples, int dataSize)
        {
            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(stream);

            var factChunkSize = totalSamples.HasValue ? 8 + 4 : 0; // id+size (8) + uint32 payload (4)
            const int fmtChunkPayloadSize = 18; // base(16) + cbSize(2)
            var riffSize = 4 + (8 + fmtChunkPayloadSize) + factChunkSize + (8 + dataSize);

            writer.Write("RIFF"u8);
            writer.Write((uint)riffSize);
            writer.Write("WAVE"u8);

            writer.Write("fmt "u8);
            writer.Write((uint)fmtChunkPayloadSize);
            writer.Write((ushort)YamahaAdpcmFormatTag);
            writer.Write((ushort)channels);
            writer.Write((uint)sampleRate);
            writer.Write((uint)(sampleRate * channels / 2)); // byte rate (nominal, not read back by WavReader)
            writer.Write((ushort)4); // block align (purely informational for this format)
            writer.Write((ushort)4); // bits per coded sample
            writer.Write((ushort)0); // cbSize: no further 'fmt ' extension data

            if (totalSamples.HasValue)
            {
                writer.Write("fact"u8);
                writer.Write((uint)4);
                writer.Write((uint)totalSamples.Value);
            }

            writer.Write("data"u8);
            writer.Write((uint)dataSize);
            writer.Write(new byte[dataSize]);
        }
    }
}
