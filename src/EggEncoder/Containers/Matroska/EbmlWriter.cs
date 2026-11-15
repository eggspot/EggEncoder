using System.Buffers.Binary;
using System.Text;

namespace EggEncoder.Containers.Matroska
{
    internal sealed class EbmlWriter
    {
        public const int PatchableSizeLength = 8;

        private readonly MemoryStream _buffer = new();

        public long Length => _buffer.Length;

        public byte[] ToArray() => _buffer.ToArray();

        public void Unsigned(long id, ulong value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
            var start = 0;
            while (start < 7 && bytes[start] == 0)
            {
                start++;
            }

            Binary(id, bytes[start..]);
        }

        public void Float(long id, double value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteDoubleBigEndian(bytes, value);
            Binary(id, bytes);
        }

        public void Text(long id, string value) => Binary(id, Encoding.UTF8.GetBytes(value));

        public void Binary(long id, ReadOnlySpan<byte> body)
        {
            WriteId(id);
            WriteSize(body.Length);
            _buffer.Write(body);
        }

        public void Master(long id, EbmlWriter children) => Binary(id, children._buffer.GetBuffer().AsSpan(0, (int)children._buffer.Length));

        public void WriteId(long id)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(bytes, id);
            var start = 0;
            while (start < 7 && bytes[start] == 0)
            {
                start++;
            }

            _buffer.Write(bytes[start..]);
        }

        public void WriteSize(long size)
        {
            var length = 1;
            while (length < 8 && size >= (1L << (7 * length)) - 1)
            {
                length++;
            }

            WriteSize(size, length);
        }

        public void WriteSize(long size, int length)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(bytes, size);
            var field = bytes[(8 - length)..];
            field[0] |= (byte)(0x80 >> (length - 1));
            _buffer.Write(field);
        }

        public void WriteUnknownSize() => _buffer.Write([0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);

        public void Raw(ReadOnlySpan<byte> bytes) => _buffer.Write(bytes);

        public static byte[] Size(long size, int length)
        {
            var writer = new EbmlWriter();
            writer.WriteSize(size, length);
            return writer.ToArray();
        }
    }
}
