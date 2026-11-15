using System.Buffers.Binary;
using System.Text;

namespace EggEncoder.Containers.Matroska
{
    internal sealed class EbmlReader
    {
        public const long UnknownSize = -1;

        private readonly Stream _stream;
        private readonly byte[] _scratch = new byte[8];
        private (long Id, long Size, long Start)? _pending;

        public EbmlReader(Stream stream)
        {
            _stream = stream;
        }

        public long Position { get; private set; }

        public bool TryReadHeader(out long id, out long size)
        {
            if (_pending is { } pending)
            {
                _pending = null;
                (id, size) = (pending.Id, pending.Size);
                return true;
            }

            if (!TryReadVint(out id, keepMarker: true, out _))
            {
                size = 0;
                return false;
            }

            if (!TryReadVint(out size, keepMarker: false, out var allOnes))
            {
                throw new InvalidDataException("Matroska: an element header is cut short");
            }

            if (allOnes)
            {
                size = UnknownSize;
            }

            return true;
        }

        public void PushBack(long id, long size) => _pending = (id, size, Position);

        public ulong ReadUnsigned(long size)
        {
            RequireSize(size, 8, "unsigned integer");
            ReadExactly(_scratch.AsSpan(0, (int)size));
            ulong value = 0;
            for (var i = 0; i < size; i++)
            {
                value = (value << 8) | _scratch[i];
            }

            return value;
        }

        public double ReadFloat(long size)
        {
            if (size == 0)
            {
                return 0;
            }

            if (size != 4 && size != 8)
            {
                throw new InvalidDataException("Matroska: a float element is neither 4 nor 8 bytes");
            }

            ReadExactly(_scratch.AsSpan(0, (int)size));
            return size == 4
                ? BinaryPrimitives.ReadSingleBigEndian(_scratch)
                : BinaryPrimitives.ReadDoubleBigEndian(_scratch);
        }

        public string ReadString(long size, int limit)
        {
            var bytes = ReadBytes(size, limit);
            var end = Array.IndexOf(bytes, (byte)0);
            return Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end);
        }

        public byte[] ReadBytes(long size, int limit)
        {
            RequireSize(size, limit, "element");
            var bytes = new byte[size];
            ReadExactly(bytes);
            return bytes;
        }

        public void Skip(long size)
        {
            if (size < 0)
            {
                throw new InvalidDataException("Matroska: an element of unknown size can't be skipped");
            }

            if (_stream.CanSeek)
            {
                if (_stream.Position + size > _stream.Length)
                {
                    throw new InvalidDataException("Matroska: an element runs past the end of the file");
                }

                _stream.Seek(size, SeekOrigin.Current);
                Position += size;
                return;
            }

            var buffer = new byte[(int)Math.Min(size, 81920)];
            while (size > 0)
            {
                var chunk = (int)Math.Min(size, buffer.Length);
                ReadExactly(buffer.AsSpan(0, chunk));
                size -= chunk;
            }
        }

        public void ReadExactly(Span<byte> buffer)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var count = _stream.Read(buffer[read..]);
                if (count == 0)
                {
                    throw new InvalidDataException("Matroska: the file ends inside an element");
                }

                read += count;
            }

            Position += buffer.Length;
        }

        private bool TryReadVint(out long value, bool keepMarker, out bool allOnes)
        {
            allOnes = false;
            var first = _stream.ReadByte();
            if (first < 0)
            {
                value = 0;
                return false;
            }

            Position++;
            if (first == 0)
            {
                throw new InvalidDataException("Matroska: a variable-length integer is longer than 8 bytes");
            }

            var length = System.Numerics.BitOperations.LeadingZeroCount((uint)first) - 23;
            var marker = 1 << (8 - length);
            long result = keepMarker ? first : first & (marker - 1);
            var ones = (first & (marker - 1)) == marker - 1;
            for (var i = 1; i < length; i++)
            {
                var next = _stream.ReadByte();
                if (next < 0)
                {
                    throw new InvalidDataException("Matroska: a variable-length integer is cut short");
                }

                Position++;
                result = (result << 8) | (uint)next;
                ones &= next == 0xFF;
            }

            allOnes = !keepMarker && ones;
            value = result;
            return true;
        }

        private static void RequireSize(long size, int limit, string what)
        {
            if (size < 0 || size > limit)
            {
                throw new InvalidDataException($"Matroska: a {what} element has an invalid size ({size} bytes)");
            }
        }
    }
}
