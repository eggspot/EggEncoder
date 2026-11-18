using System.Buffers.Binary;

namespace EggEncoder.Transform
{
    public sealed class BitWriter
    {
        private byte[] _buffer = new byte[64];
        private int _byteLength;
        private ulong _pending;
        private int _pendingBits;

        public int BitPosition => (_byteLength * 8) + _pendingBits;

        public void WriteBits(uint value, int bitCount)
        {
            if (bitCount > 32)
            {
                WriteBitsOneByOne(value, bitCount);
                return;
            }

            if (bitCount <= 0)
            {
                return;
            }

            var masked = bitCount == 32 ? value : value & ((1u << bitCount) - 1);
            _pending = (_pending << bitCount) | masked;
            _pendingBits += bitCount;
            if (_pendingBits >= 32)
            {
                _pendingBits -= 32;
                AppendWord((uint)(_pending >> _pendingBits));
                _pending &= (1UL << _pendingBits) - 1;
            }
        }

        internal void WriteBits(ulong value, int bitCount)
        {
            if (bitCount > 32)
            {
                WriteBits((uint)(value >> 32), bitCount - 32);
                WriteBits((uint)value, 32);
                return;
            }

            WriteBits((uint)value, bitCount);
        }

        public void WriteSignedBits(int value, int bitCount)
        {
            var mask = bitCount == 32 ? 0xFFFFFFFFu : (1u << bitCount) - 1;
            WriteBits((uint)value & mask, bitCount);
        }

        public void WriteUnary(uint zeros)
        {
            WriteBits(0, (int)zeros);
            WriteBits(1, 1);
        }

        public void ByteAlign()
        {
            var bitsInByte = _pendingBits % 8;
            if (bitsInByte == 0)
            {
                return;
            }

            WriteBits(0, 8 - bitsInByte);
        }

        public byte[] ToArray()
        {
            var wholeBytes = _pendingBits / 8;
            var leftoverBits = _pendingBits % 8;
            var result = new byte[_byteLength + wholeBytes + (leftoverBits > 0 ? 1 : 0)];
            Array.Copy(_buffer, result, _byteLength);
            for (var i = 0; i < wholeBytes; i++)
            {
                result[_byteLength + i] = (byte)(_pending >> (_pendingBits - (8 * (i + 1))));
            }

            if (leftoverBits > 0)
            {
                result[^1] = (byte)(_pending << (8 - leftoverBits));
            }

            return result;
        }

        private void WriteBitsOneByOne(uint value, int bitCount)
        {
            if (value == 0)
            {
                for (; bitCount > 32; bitCount -= 32)
                {
                    WriteBits(0u, 32);
                }

                WriteBits(0u, bitCount);
                return;
            }

            for (var i = bitCount - 1; i >= 0; i--)
            {
                WriteBits((value >> i) & 1u, 1);
            }
        }

        private void AppendWord(uint value)
        {
            if (_byteLength + 4 > _buffer.Length)
            {
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _byteLength + 4));
            }

            BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(_byteLength), value);
            _byteLength += 4;
        }
    }
}
