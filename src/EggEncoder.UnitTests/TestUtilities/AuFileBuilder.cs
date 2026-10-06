using System.Buffers.Binary;

namespace EggEncoder.UnitTests.TestUtilities
{
    // Hand-builds minimal/malformed AU (.snd magic) files directly from their byte layout --
    // deliberately independent of AuWriter -- so AuReaderTest exercises the reader against a
    // known-correct file it didn't also help produce. Mirrors AiffFileBuilder's role for plain AIFF.
    public static class AuFileBuilder
    {
        /// <summary>Builds a well-formed AU file with no annotation string (headerSize = 24) and an exact declared dataSize.</summary>
        public static void Create(string filePath, int channels, int sampleRate, uint encoding, byte[] sampleDataBytes)
        {
            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(stream);

            writer.Write(".snd"u8);
            WriteUInt32BigEndian(writer, 24);
            WriteUInt32BigEndian(writer, (uint)sampleDataBytes.Length);
            WriteUInt32BigEndian(writer, encoding);
            WriteUInt32BigEndian(writer, (uint)sampleRate);
            WriteUInt32BigEndian(writer, (uint)channels);
            writer.Write(sampleDataBytes);
        }

        /// <summary>Same as <see cref="Create"/>, but with an explicit headerSize/annotation region and AU's own "unknown size" sentinel (0xFFFFFFFF) for dataSize, to prove a real streaming-style file is still read correctly.</summary>
        public static void CreateWithAnnotationAndUnknownDataSize(string filePath, int channels, int sampleRate, uint encoding, byte[] sampleDataBytes, int annotationLength)
        {
            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(stream);

            var headerSize = 24 + annotationLength;

            writer.Write(".snd"u8);
            WriteUInt32BigEndian(writer, (uint)headerSize);
            WriteUInt32BigEndian(writer, 0xFFFFFFFF); // unknown data size -- read until EOF
            WriteUInt32BigEndian(writer, encoding);
            WriteUInt32BigEndian(writer, (uint)sampleRate);
            WriteUInt32BigEndian(writer, (uint)channels);
            writer.Write(new byte[annotationLength]);
            writer.Write(sampleDataBytes);
        }

        /// <summary>A header declaring a headerSize smaller than AU's own mandatory 24-byte fixed header.</summary>
        public static void CreateWithTooSmallHeaderSize(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(stream);

            writer.Write(".snd"u8);
            WriteUInt32BigEndian(writer, 16); // too small -- must be >= 24
            WriteUInt32BigEndian(writer, 4);
            WriteUInt32BigEndian(writer, 3);
            WriteUInt32BigEndian(writer, 44100);
            WriteUInt32BigEndian(writer, 1);
            writer.Write(new byte[4]);
        }

        private static void WriteUInt32BigEndian(BinaryWriter writer, uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            writer.Write(bytes);
        }
    }
}
