using System.Buffers.Binary;
using EggEncoder.Codecs.Aiff;

namespace EggEncoder.UnitTests.TestUtilities
{
    // Builds a plain AIFF (FORM/COMM/SSND) file directly from its byte layout -- deliberately
    // independent of AiffWriter -- so AiffReaderTest exercises the reader against a known-correct file
    // it didn't also help produce. Mirrors WavFileBuilder's role for WavReaderTest.
    public static class AiffFileBuilder
    {
        public static void Create(string filePath, int channels, int sampleRate, int bitsPerSample, int[] interleavedSamples)
        {
            var bytesPerSample = bitsPerSample / 8;
            var totalFrames = interleavedSamples.Length / channels;
            var dataSize = interleavedSamples.Length * bytesPerSample;
            var needsPadByte = dataSize % 2 != 0;

            const int commChunkSize = 18;
            var ssndChunkSize = 8 + dataSize;
            var formSize = 4 + (8 + commChunkSize) + (8 + ssndChunkSize) + (needsPadByte ? 1 : 0);

            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(stream);

            writer.Write("FORM"u8);
            WriteUInt32BigEndian(writer, (uint)formSize);
            writer.Write("AIFF"u8);

            writer.Write("COMM"u8);
            WriteUInt32BigEndian(writer, commChunkSize);
            WriteInt16BigEndian(writer, (short)channels);
            WriteUInt32BigEndian(writer, (uint)totalFrames);
            WriteInt16BigEndian(writer, (short)bitsPerSample);
            Span<byte> sampleRateBytes = stackalloc byte[10];
            IeeeExtendedFloat.FromDouble(sampleRate, sampleRateBytes);
            writer.Write(sampleRateBytes);

            writer.Write("SSND"u8);
            WriteUInt32BigEndian(writer, (uint)ssndChunkSize);
            WriteUInt32BigEndian(writer, 0); // offset
            WriteUInt32BigEndian(writer, 0); // blockSize

            foreach (var sample in interleavedSamples)
            {
                switch (bytesPerSample)
                {
                    case 1:
                        writer.Write(unchecked((byte)sample));
                        break;
                    case 2:
                        WriteInt16BigEndian(writer, (short)sample);
                        break;
                    case 3:
                        writer.Write((byte)(sample >> 16));
                        writer.Write((byte)(sample >> 8));
                        writer.Write((byte)sample);
                        break;
                    case 4:
                        WriteUInt32BigEndian(writer, unchecked((uint)sample));
                        break;
                    default:
                        throw new NotSupportedException($"Unsupported bytes per sample: {bytesPerSample}");
                }
            }

            if (needsPadByte)
            {
                writer.Write((byte)0);
            }
        }

        private static void WriteUInt32BigEndian(BinaryWriter writer, uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            writer.Write(bytes);
        }

        private static void WriteInt16BigEndian(BinaryWriter writer, short value)
        {
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteInt16BigEndian(bytes, value);
            writer.Write(bytes);
        }
    }
}
