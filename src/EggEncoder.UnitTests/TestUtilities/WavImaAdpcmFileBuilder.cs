namespace EggEncoder.UnitTests.TestUtilities
{
    // Hand-builds minimal/malformed IMA ADPCM (format tag 17) WAV files for WavReader's own
    // validation-path tests -- unlike WavFileBuilder's plain-PCM files, these need the extra fmt-chunk
    // fields (cbSize/wSamplesPerBlock) and an optional 'fact' chunk, which no existing helper covers.
    public static class WavImaAdpcmFileBuilder
    {
        private const int ImaAdpcmFormatTag = 17;

        public static void CreateMinimal(string filePath, int channels, int sampleRate, int blockAlign, int samplesPerBlock, long? totalSamples, int blockCount = 1, int truncateLastBlockBytes = 0)
        {
            var dataSize = (blockCount * blockAlign) - truncateLastBlockBytes;

            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(stream);

            var factChunkSize = totalSamples.HasValue ? 8 + 4 : 0; // id+size (8) + uint32 payload (4)
            var riffSize = 4 + (8 + 20) + factChunkSize + (8 + dataSize);

            writer.Write("RIFF"u8);
            writer.Write((uint)riffSize);
            writer.Write("WAVE"u8);

            writer.Write("fmt "u8);
            writer.Write((uint)20);
            writer.Write((ushort)ImaAdpcmFormatTag);
            writer.Write((ushort)channels);
            writer.Write((uint)sampleRate);
            writer.Write((uint)(sampleRate * blockAlign)); // byte rate (nominal, not read back by WavReader)
            writer.Write((ushort)blockAlign);
            writer.Write((ushort)4); // bits per coded sample
            writer.Write((ushort)2); // cbSize
            writer.Write((ushort)samplesPerBlock);

            if (totalSamples.HasValue)
            {
                writer.Write("fact"u8);
                writer.Write((uint)4);
                writer.Write((uint)totalSamples.Value);
            }

            writer.Write("data"u8);
            writer.Write((uint)dataSize);

            // Each channel's own 4-byte header (predictor=0, step_index=0, reserved=0) followed by
            // zero-filled nibble data -- decodes to a flat, silent block, which is all these
            // validation-focused fixtures need.
            var zeroBytes = new byte[dataSize];
            writer.Write(zeroBytes);
        }

        public static void CreateWithTruncatedFmtChunk(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(stream);

            const int dataSize = 1024;
            var riffSize = 4 + (8 + 16) + (8 + dataSize);

            writer.Write("RIFF"u8);
            writer.Write((uint)riffSize);
            writer.Write("WAVE"u8);

            writer.Write("fmt "u8);
            writer.Write((uint)16); // plain, PCM-sized fmt chunk -- no cbSize/wSamplesPerBlock at all
            writer.Write((ushort)ImaAdpcmFormatTag);
            writer.Write((ushort)1);
            writer.Write((uint)44100);
            writer.Write((uint)22050);
            writer.Write((ushort)1024);
            writer.Write((ushort)4);

            writer.Write("data"u8);
            writer.Write((uint)dataSize);
            writer.Write(new byte[dataSize]);
        }
    }
}
