namespace EggEncoder.UnitTests.TestUtilities
{
    public static class WavFileBuilder
    {
        public static void Create(string filePath, int channels, int sampleRate, int bitsPerSample, int[] interleavedSamples)
        {
            var bytesPerSample = bitsPerSample / 8;
            var dataSize = interleavedSamples.Length * bytesPerSample;
            var byteRate = sampleRate * channels * bytesPerSample;
            var blockAlign = channels * bytesPerSample;

            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(stream);

            writer.Write("RIFF"u8);
            writer.Write((uint)(36 + dataSize));
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write((uint)16);
            writer.Write((ushort)1);
            writer.Write((ushort)channels);
            writer.Write((uint)sampleRate);
            writer.Write((uint)byteRate);
            writer.Write((ushort)blockAlign);
            writer.Write((ushort)bitsPerSample);
            writer.Write("data"u8);
            writer.Write((uint)dataSize);

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
                    default:
                        throw new NotSupportedException($"Unsupported bytes per sample: {bytesPerSample}");
                }
            }
        }
    }
}
