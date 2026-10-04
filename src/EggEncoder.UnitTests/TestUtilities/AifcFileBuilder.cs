using System.Buffers.Binary;
using System.Text;
using EggEncoder.Codecs.Aiff;

namespace EggEncoder.UnitTests.TestUtilities
{
    // Hand-builds minimal/malformed AIFC (FORM/AIFC) files directly from their byte layout --
    // deliberately independent of AiffWriter -- so AiffReaderTest exercises the reader against a
    // known-correct file it didn't also help produce. Mirrors AiffFileBuilder's role for plain AIFF.
    //
    // compressionName is deliberately never written as a non-empty value here: AiffReader never reads
    // it (see AiffReader's own doc comment on why), so there is nothing about a non-empty name this
    // builder could usefully exercise. The FVER chunk is likewise optional as far as AiffReader is
    // concerned -- it never requires or validates it -- but Create writes a real one anyway so the
    // fixtures this builder produces for decode tests look like a genuine AIFC file, not just the
    // minimum AiffReader happens to tolerate.
    public static class AifcFileBuilder
    {
        /// <summary>
        /// Builds a well-formed AIFC file. <paramref name="sampleDataBytes"/>'s length must be an exact
        /// whole number of <paramref name="channels"/>-channel frames at <paramref name="sampleSize"/>
        /// bits per coded sample -- true for every compressionType this builder (and AiffReader) knows
        /// about, where the on-disk byte width per sample is always exactly <paramref name="sampleSize"/> / 8.
        /// </summary>
        public static void Create(string filePath, int channels, int sampleRate, int sampleSize, string compressionType, byte[] sampleDataBytes)
        {
            var totalFrames = sampleDataBytes.Length / (sampleSize / 8) / channels;
            CreateWithExplicitFrameCount(filePath, channels, sampleRate, sampleSize, compressionType, sampleDataBytes, totalFrames);
        }

        /// <summary>Same as <see cref="Create"/>, but with an explicit frame count instead of deriving one from byte length -- for a test that deliberately wants the COMM chunk's own numSampleFrames to disagree with what the SSND data actually holds.</summary>
        public static void CreateWithExplicitFrameCount(string filePath, int channels, int sampleRate, int sampleSize, string compressionType, byte[] sampleDataBytes, int totalFrames)
        {
            var dataSize = sampleDataBytes.Length;
            var needsPadByte = dataSize % 2 != 0;

            const int commChunkSize = 24; // base(18) + compressionType(4) + empty compressionName (1 length byte + 1 pad byte)
            const int fverChunkTotalSize = 8 + 4;
            var ssndChunkSize = 8 + dataSize;
            var formSize = 4 + fverChunkTotalSize + (8 + commChunkSize) + (8 + ssndChunkSize) + (needsPadByte ? 1 : 0);

            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(stream);

            writer.Write("FORM"u8);
            WriteUInt32BigEndian(writer, (uint)formSize);
            writer.Write("AIFC"u8);

            writer.Write("FVER"u8);
            WriteUInt32BigEndian(writer, 4);
            WriteUInt32BigEndian(writer, 0xA2805140); // AIFF-C format version timestamp, the one real encoders always write

            writer.Write("COMM"u8);
            WriteUInt32BigEndian(writer, commChunkSize);
            WriteInt16BigEndian(writer, (short)channels);
            WriteUInt32BigEndian(writer, (uint)totalFrames);
            WriteInt16BigEndian(writer, (short)sampleSize);
            Span<byte> sampleRateBytes = stackalloc byte[10];
            IeeeExtendedFloat.FromDouble(sampleRate, sampleRateBytes);
            writer.Write(sampleRateBytes);
            writer.Write(Encoding.ASCII.GetBytes(compressionType));
            writer.Write((byte)0); // compressionName length (empty)
            writer.Write((byte)0); // pad byte (1 + 0 is odd)

            writer.Write("SSND"u8);
            WriteUInt32BigEndian(writer, (uint)ssndChunkSize);
            WriteUInt32BigEndian(writer, 0); // offset
            WriteUInt32BigEndian(writer, 0); // blockSize
            writer.Write(sampleDataBytes);

            if (needsPadByte)
            {
                writer.Write((byte)0);
            }
        }

        /// <summary>A COMM chunk declaring a chunkSize of exactly 18 (plain AIFF's own base size) under an AIFC form type -- too short to carry the mandatory compressionType field at all.</summary>
        public static void CreateWithCommChunkTooShortForCompressionType(string filePath)
        {
            const int dataSize = 4;
            const int commChunkSize = 18;
            var ssndChunkSize = 8 + dataSize;
            var formSize = 4 + (8 + commChunkSize) + (8 + ssndChunkSize);

            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(stream);

            writer.Write("FORM"u8);
            WriteUInt32BigEndian(writer, (uint)formSize);
            writer.Write("AIFC"u8);

            writer.Write("COMM"u8);
            WriteUInt32BigEndian(writer, commChunkSize);
            WriteInt16BigEndian(writer, 1);
            WriteUInt32BigEndian(writer, 1);
            WriteInt16BigEndian(writer, 16);
            Span<byte> sampleRateBytes = stackalloc byte[10];
            IeeeExtendedFloat.FromDouble(44100, sampleRateBytes);
            writer.Write(sampleRateBytes);

            writer.Write("SSND"u8);
            WriteUInt32BigEndian(writer, (uint)ssndChunkSize);
            WriteUInt32BigEndian(writer, 0);
            WriteUInt32BigEndian(writer, 0);
            writer.Write(new byte[dataSize]);
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
