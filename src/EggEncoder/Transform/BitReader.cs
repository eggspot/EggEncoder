using System.Buffers.Binary;
using System.Numerics;

namespace EggEncoder.Transform
{
    public sealed class BitReader
    {
        private readonly byte[] _buffer;
        private readonly int _bitLength;

        private int _bitPosition;

        public BitReader(byte[] buffer, int byteOffset = 0, int? byteLength = null)
        {
            _buffer = buffer;

            var length = byteLength ?? (buffer.Length - byteOffset);
            _bitPosition = byteOffset * 8;
            _bitLength = _bitPosition + (length * 8);
        }

        public int BitPosition => _bitPosition;

        public int RemainingBits => _bitLength - _bitPosition;

        public uint ReadBits(int bitCount)
        {
            if (bitCount is < 0 or > 32)
            {
                throw new ArgumentOutOfRangeException(nameof(bitCount), bitCount, "Bit count must be between 0 and 32");
            }

            if (RemainingBits < bitCount)
            {
                throw new EndOfStreamException("Attempted to read past the end of the bitstream");
            }

            if (bitCount == 0)
            {
                return 0;
            }

            // One big-endian load of the (at most 5) bytes spanning the requested bits, rather than
            // one loop iteration per bit: bitOffset (0-7) + bitCount (1-32) never exceeds 64.
            var bitOffset = _bitPosition & 7;
            var window = LoadWindow(_bitPosition >> 3, (bitOffset + bitCount + 7) >> 3);
            _bitPosition += bitCount;

            return (uint)((window << bitOffset) >> (64 - bitCount));
        }

        public uint PeekBits(int bitCount)
        {
            var savedPosition = _bitPosition;
            var value = ReadBits(bitCount);
            _bitPosition = savedPosition;

            return value;
        }

        public int ReadSignedBits(int count)
        {
            var shift = 32 - count;
            return (int)(ReadBits(count) << shift) >> shift;
        }

        public uint ReadUnary()
        {
            var zeros = 0u;
            while (true)
            {
                var remainingBits = RemainingBits;
                if (remainingBits <= 0)
                {
                    throw new EndOfStreamException("Attempted to read past the end of the bitstream");
                }

                // Scan up to one 64-bit window at a time. Bits past _bitLength may still be in the
                // buffer (a byteLength-limited reader), so they're masked off rather than counted.
                // The window also stops at the buffer's own end, so a reader whose byteLength runs
                // past its buffer still finds a one bit in the bytes it does have, and only fails
                // (IndexOutOfRangeException, as it always did) on reaching a missing byte.
                var bitOffset = _bitPosition & 7;
                var bufferBits = (_buffer.Length * 8L) - _bitPosition;
                var validBits = (int)Math.Min(Math.Min(64 - bitOffset, remainingBits), Math.Max(bufferBits, 1));
                var window = LoadWindow(_bitPosition >> 3, (bitOffset + validBits + 7) >> 3) << bitOffset;
                window &= ulong.MaxValue << (64 - validBits);

                if (window != 0)
                {
                    var leadingZeros = BitOperations.LeadingZeroCount(window);
                    _bitPosition += leadingZeros + 1;
                    return zeros + (uint)leadingZeros;
                }

                zeros += (uint)validBits;
                _bitPosition += validBits;
            }
        }

        public void SkipBits(int bitCount)
        {
            if (RemainingBits < bitCount)
            {
                throw new EndOfStreamException("Attempted to skip past the end of the bitstream");
            }

            _bitPosition += bitCount;
        }

        public void ByteAlign()
        {
            _bitPosition = (_bitPosition + 7) / 8 * 8;
        }

        public void SkipToBitPosition(int bitPosition)
        {
            if (bitPosition < _bitPosition)
            {
                throw new ArgumentOutOfRangeException(nameof(bitPosition), bitPosition, "Cannot skip backwards in the bitstream");
            }

            if (bitPosition > _bitLength)
            {
                throw new EndOfStreamException("Attempted to skip past the end of the bitstream");
            }

            _bitPosition = bitPosition;
        }

        // Loads byteCount (1-8) bytes starting at byteIndex as a big-endian value aligned to the top
        // of a ulong, with zeros below them. Reads a full 8 bytes when the buffer has them (the
        // common case, a single load); otherwise only the bytes asked for, so a reader whose
        // byteLength runs past its buffer still fails with the same IndexOutOfRangeException it
        // always did rather than silently reading zeros.
        private ulong LoadWindow(int byteIndex, int byteCount)
        {
            if (byteIndex >= 0 && byteIndex <= _buffer.Length - 8)
            {
                return BinaryPrimitives.ReadUInt64BigEndian(_buffer.AsSpan(byteIndex, 8));
            }

            var window = 0UL;
            for (var i = 0; i < byteCount; i++)
            {
                window |= (ulong)_buffer[byteIndex + i] << (56 - (8 * i));
            }

            return window;
        }
    }
}
