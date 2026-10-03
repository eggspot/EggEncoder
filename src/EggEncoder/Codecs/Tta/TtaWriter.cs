using System.Buffers.Binary;
using System.Text;

namespace EggEncoder.Codecs.Tta
{
    // Writes a TTA (True Audio) file -- see TtaReader's doc comment for the format. The whole frame
    // list is known by the time this is called (see TtaEncoderSession), so this writes the file in
    // one pass: header, seek table (needs every frame's final compressed size up front), then each
    // frame's data immediately followed by its own CRC32.
    internal static class TtaWriter
    {
        public static void Write(Stream destination, int channels, int bitsPerSample, int sampleRate, long totalSamples, IReadOnlyList<byte[]> frames)
        {
            using var writer = new BinaryWriter(destination, Encoding.ASCII, leaveOpen: true);

            Span<byte> header = stackalloc byte[18];
            "TTA1"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt16LittleEndian(header[4..], 1); // format = PCM
            BinaryPrimitives.WriteUInt16LittleEndian(header[6..], (ushort)channels);
            BinaryPrimitives.WriteUInt16LittleEndian(header[8..], (ushort)bitsPerSample);
            BinaryPrimitives.WriteUInt32LittleEndian(header[10..], (uint)sampleRate);
            BinaryPrimitives.WriteUInt32LittleEndian(header[14..], (uint)totalSamples);

            writer.Write(header);
            WriteUInt32LittleEndian(writer, Crc32.Compute(header));

            var seekTable = new byte[frames.Count * 4];
            for (var i = 0; i < frames.Count; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(seekTable.AsSpan(i * 4), (uint)(frames[i].Length + 4));
            }

            writer.Write(seekTable);
            WriteUInt32LittleEndian(writer, Crc32.Compute(seekTable));

            foreach (var frame in frames)
            {
                writer.Write(frame);
                WriteUInt32LittleEndian(writer, Crc32.Compute(frame));
            }
        }

        private static void WriteUInt32LittleEndian(BinaryWriter writer, uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            writer.Write(bytes);
        }
    }
}
