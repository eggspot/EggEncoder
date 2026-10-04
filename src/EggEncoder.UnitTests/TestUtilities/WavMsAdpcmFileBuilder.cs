namespace EggEncoder.UnitTests.TestUtilities
{
    // Hand-builds minimal/malformed MS ADPCM (format tag 2) WAV files for WavReader's own
    // validation-path tests -- unlike WavFileBuilder's plain-PCM files, these need the extra fmt-chunk
    // fields (cbSize/wSamplesPerBlock/wNumCoef + the coefficient table itself) and an optional 'fact'
    // chunk, which no existing helper covers.
    public static class WavMsAdpcmFileBuilder
    {
        private const int MsAdpcmFormatTag = 2;

        // The real, standard 7-pair coefficient table every real encoder emits (verified against a
        // real ffmpeg-produced file's own fmt chunk bytes) -- used as the default table so a
        // validation-focused fixture's block predictor byte (always 0 here) resolves to a real,
        // meaningful (Coeff1=256, Coeff2=0) entry rather than an arbitrary placeholder.
        private static readonly short[] _standardCoeff1 = [256, 512, 0, 192, 240, 460, 392];
        private static readonly short[] _standardCoeff2 = [0, -256, 0, 64, 0, -208, -232];

        public static void CreateMinimal(string filePath, int channels, int sampleRate, int blockAlign, int samplesPerBlock, long? totalSamples, int blockCount = 1, int truncateLastBlockBytes = 0, int numCoef = 7)
        {
            var dataSize = (blockCount * blockAlign) - truncateLastBlockBytes;

            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(stream);

            var fmtExtensionSize = 4 + (numCoef * 4); // samplesPerBlock(2) + numCoef(2) + numCoef*(iCoef1+iCoef2)
            var fmtChunkPayloadSize = 16 + 2 + fmtExtensionSize; // base(16) + cbSize field itself (2) + extension
            var factChunkSize = totalSamples.HasValue ? 8 + 4 : 0;
            var riffSize = 4 + (8 + fmtChunkPayloadSize) + factChunkSize + (8 + dataSize);

            writer.Write("RIFF"u8);
            writer.Write((uint)riffSize);
            writer.Write("WAVE"u8);

            writer.Write("fmt "u8);
            writer.Write((uint)fmtChunkPayloadSize);
            writer.Write((ushort)MsAdpcmFormatTag);
            writer.Write((ushort)channels);
            writer.Write((uint)sampleRate);
            writer.Write((uint)(sampleRate * blockAlign)); // byte rate (nominal, not read back by WavReader)
            writer.Write((ushort)blockAlign);
            writer.Write((ushort)4); // bits per coded sample
            writer.Write((ushort)fmtExtensionSize);
            writer.Write((ushort)samplesPerBlock);
            writer.Write((ushort)numCoef);

            for (var i = 0; i < numCoef; i++)
            {
                var index = i % _standardCoeff1.Length;
                writer.Write(_standardCoeff1[index]);
                writer.Write(_standardCoeff2[index]);
            }

            if (totalSamples.HasValue)
            {
                writer.Write("fact"u8);
                writer.Write((uint)4);
                writer.Write((uint)totalSamples.Value);
            }

            writer.Write("data"u8);
            writer.Write((uint)dataSize);

            // Each channel's own header (block predictor=0, delta=16, sample1=0, sample2=0, grouped
            // by field across channels) followed by zero-filled nibble data -- decodes to a flat,
            // silent block, which is all these validation-focused fixtures need. Block predictor 0
            // resolves to the standard table's (Coeff1=256, Coeff2=0) entry.
            var block = new byte[dataSize];
            for (var b = 0; b < blockCount; b++)
            {
                var offset = b * blockAlign;
                if (offset + (7 * channels) > dataSize)
                {
                    break;
                }

                for (var ch = 0; ch < channels; ch++)
                {
                    block[offset + ch] = 0; // block predictor
                }

                for (var ch = 0; ch < channels; ch++)
                {
                    block[offset + channels + (ch * 2)] = 16; // delta low byte = 16
                    block[offset + channels + (ch * 2) + 1] = 0;
                }
            }

            writer.Write(block);
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
            writer.Write((uint)16); // plain, PCM-sized fmt chunk -- no cbSize/wSamplesPerBlock/coefficients at all
            writer.Write((ushort)MsAdpcmFormatTag);
            writer.Write((ushort)1);
            writer.Write((uint)44100);
            writer.Write((uint)22050);
            writer.Write((ushort)1024);
            writer.Write((ushort)4);

            writer.Write("data"u8);
            writer.Write((uint)dataSize);
            writer.Write(new byte[dataSize]);
        }

        // Declares a real wNumCoef (7) but a 'fmt ' chunk size too short to actually carry all seven
        // coefficient pairs -- structurally distinct from CreateWithTruncatedFmtChunk (which has no
        // extension at all).
        public static void CreateWithTruncatedCoefficientTable(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(stream);

            const int dataSize = 1024;
            const int declaredFmtChunkSize = 26; // 16 base + 2 cbSize + 2 samplesPerBlock + 2 numCoef + only 4 bytes (1 coefficient pair) of real room
            var riffSize = 4 + (8 + declaredFmtChunkSize) + (8 + dataSize);

            writer.Write("RIFF"u8);
            writer.Write((uint)riffSize);
            writer.Write("WAVE"u8);

            writer.Write("fmt "u8);
            writer.Write((uint)declaredFmtChunkSize);
            writer.Write((ushort)MsAdpcmFormatTag);
            writer.Write((ushort)1);
            writer.Write((uint)44100);
            writer.Write((uint)22050);
            writer.Write((ushort)1024);
            writer.Write((ushort)4);
            writer.Write((ushort)6); // cbSize: samplesPerBlock(2) + numCoef(2) + only 1 coefficient pair's worth (4)... declared short
            writer.Write((ushort)2036);
            writer.Write((ushort)7); // claims 7 coefficient pairs, but the chunk has no room for them
            writer.Write(_standardCoeff1[0]);
            writer.Write(_standardCoeff2[0]);

            writer.Write("data"u8);
            writer.Write((uint)dataSize);
            writer.Write(new byte[dataSize]);
        }

        // Declares a real chunkSize with plenty of physical room (22 bytes, enough for cbSize +
        // wSamplesPerBlock + wNumCoef), but a cbSize field that itself claims fewer than the 4 bytes
        // needed for wSamplesPerBlock/wNumCoef -- a mismatch between the chunk's own declared size and
        // its own cbSize sub-field that CreateWithTruncatedFmtChunk (no extension at all) and
        // CreateWithTruncatedCoefficientTable (chunkSize too short) don't exercise.
        public static void CreateWithCbSizeTooShort(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(stream);

            const int declaredFmtChunkSize = 22;

            writer.Write("RIFF"u8);
            writer.Write((uint)0); // not validated by WavReader; Open() throws before any chunk after this is read
            writer.Write("WAVE"u8);

            writer.Write("fmt "u8);
            writer.Write((uint)declaredFmtChunkSize);
            writer.Write((ushort)MsAdpcmFormatTag);
            writer.Write((ushort)1);
            writer.Write((uint)44100);
            writer.Write((uint)22050);
            writer.Write((ushort)1024);
            writer.Write((ushort)4);
            writer.Write((ushort)2); // cbSize=2, below the 4-byte minimum for wSamplesPerBlock+wNumCoef
        }

        public static void CreateWithZeroCoefficients(string filePath)
        {
            CreateMinimal(filePath, channels: 1, sampleRate: 44100, blockAlign: 1024, samplesPerBlock: 2036, totalSamples: 1, numCoef: 0);
        }
    }
}
