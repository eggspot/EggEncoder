namespace EggEncoder.UnitTests.TestUtilities
{
    public static class WavFileBuilder
    {
        public static void Create(string filePath, int channels, int sampleRate, int bitsPerSample, int[] interleavedSamples)
        {
            var bytesPerSample = bitsPerSample / 8;
            var dataSize = interleavedSamples.Length * bytesPerSample;

            WriteHeader(filePath, formatTag: 1, channels, sampleRate, bitsPerSample, dataSize, out var writer, out var stream);
            using (stream)
            using (writer)
            {
                foreach (var sample in interleavedSamples)
                {
                    switch (bytesPerSample)
                    {
                        case 1:
                            writer.Write((byte)sample);
                            break;
                        case 2:
                            writer.Write((short)sample);
                            break;
                        case 3:
                            writer.Write((byte)sample);
                            writer.Write((byte)(sample >> 8));
                            writer.Write((byte)(sample >> 16));
                            break;
                        case 4:
                            writer.Write(sample);
                            break;
                        default:
                            throw new NotSupportedException($"Unsupported bytes per sample: {bytesPerSample}");
                    }
                }
            }
        }

        public static void CreateFloat32(string filePath, int channels, int sampleRate, float[] interleavedSamples)
        {
            var dataSize = interleavedSamples.Length * 4;

            WriteHeader(filePath, formatTag: 3, channels, sampleRate, bitsPerSample: 32, dataSize, out var writer, out var stream);
            using (stream)
            using (writer)
            {
                foreach (var sample in interleavedSamples)
                {
                    writer.Write(sample);
                }
            }
        }

        private static void WriteHeader(string filePath, ushort formatTag, int channels, int sampleRate, int bitsPerSample, int dataSize, out BinaryWriter writer, out FileStream stream)
        {
            var bytesPerSample = bitsPerSample / 8;
            var byteRate = sampleRate * channels * bytesPerSample;
            var blockAlign = channels * bytesPerSample;

            stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            writer = new BinaryWriter(stream);

            writer.Write("RIFF"u8);
            writer.Write((uint)(36 + dataSize));
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write((uint)16);
            writer.Write(formatTag);
            writer.Write((ushort)channels);
            writer.Write((uint)sampleRate);
            writer.Write((uint)byteRate);
            writer.Write((ushort)blockAlign);
            writer.Write((ushort)bitsPerSample);
            writer.Write("data"u8);
            writer.Write((uint)dataSize);
        }
    }
}
