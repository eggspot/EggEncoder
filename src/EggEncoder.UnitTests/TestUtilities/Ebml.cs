using System.Buffers.Binary;
using System.Text;

namespace EggEncoder.UnitTests.TestUtilities
{
    internal static class Ebml
    {
        public static byte[] File(string docType, params byte[][] segmentChildren) =>
            [.. Element(0x1A45DFA3, Text(0x4282, docType)), .. UnknownSize(0x18538067, segmentChildren)];

        public static byte[] Element(long id, params byte[][] children)
        {
            var body = children.SelectMany(c => c).ToArray();
            return [.. Id(id), .. Size(body.Length), .. body];
        }

        public static byte[] UnknownSize(long id, params byte[][] children) =>
            [.. Id(id), 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, .. children.SelectMany(c => c)];

        public static byte[] Uint(long id, ulong value)
        {
            var bytes = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
            var start = 0;
            while (start < 7 && bytes[start] == 0)
            {
                start++;
            }

            return Raw(id, bytes[start..]);
        }

        public static byte[] Float(long id, double value)
        {
            var bytes = new byte[8];
            BinaryPrimitives.WriteDoubleBigEndian(bytes, value);
            return Raw(id, bytes);
        }

        public static byte[] Text(long id, string value) => Raw(id, Encoding.UTF8.GetBytes(value));

        public static byte[] Raw(long id, byte[] body) => [.. Id(id), .. Size(body.Length), .. body];

        public static byte[] Block(long id, int track, short time, byte flags, byte[] laceHeader, byte[][] frames)
        {
            var body = new List<byte> { (byte)(0x80 | track), (byte)(time >> 8), (byte)time, flags };
            if ((flags & 0x06) != 0)
            {
                body.Add((byte)(frames.Length - 1));
                body.AddRange(laceHeader);
            }

            foreach (var frame in frames)
            {
                body.AddRange(frame);
            }

            return Raw(id, [.. body]);
        }

        private static byte[] Id(long id)
        {
            var bytes = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(bytes, id);
            var start = 0;
            while (start < 7 && bytes[start] == 0)
            {
                start++;
            }

            return bytes[start..];
        }

        private static byte[] Size(long size)
        {
            var bytes = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(bytes, size);
            bytes[0] = 0x01;
            return bytes;
        }
    }
}
