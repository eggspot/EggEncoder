namespace EggEncoder.Transform
{
    public sealed class BitWriter
    {
        private byte[] _buffer = new byte[64];
        private int _byteLength;
        private uint _currentByte;
        private int _bitsInCurrentByte;

        public int BitPosition => (_byteLength * 8) + _bitsInCurrentByte;

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
            var accumulated = ((ulong)_currentByte << bitCount) | masked;
            var total = _bitsInCurrentByte + bitCount;
            while (total >= 8)
            {
                total -= 8;
                AppendByte((byte)(accumulated >> total));
            }

            _currentByte = (uint)(accumulated & ((1UL << total) - 1));
            _bitsInCurrentByte = total;
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
            if (_bitsInCurrentByte == 0)
            {
                return;
            }

            WriteBits(0, 8 - _bitsInCurrentByte);
        }

        public byte[] ToArray()
        {
            var length = _byteLength + (_bitsInCurrentByte > 0 ? 1 : 0);
            var result = new byte[length];

            Array.Copy(_buffer, result, _byteLength);

            if (_bitsInCurrentByte > 0)
            {
                result[_byteLength] = (byte)(_currentByte << (8 - _bitsInCurrentByte));
            }

            return result;
        }

        private void WriteBitsOneByOne(uint value, int bitCount)
        {
            for (var i = bitCount - 1; i >= 0; i--)
            {
                WriteBits((value >> i) & 1u, 1);
            }
        }

        private void AppendByte(byte value)
        {
            if (_byteLength == _buffer.Length)
            {
                Array.Resize(ref _buffer, _buffer.Length * 2);
            }

            _buffer[_byteLength++] = value;
        }
    }
}
