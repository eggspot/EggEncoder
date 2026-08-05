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
            for (var i = bitCount - 1; i >= 0; i--)
            {
                var bit = (value >> i) & 1u;
                _currentByte = (_currentByte << 1) | bit;
                _bitsInCurrentByte++;

                if (_bitsInCurrentByte == 8)
                {
                    AppendByte((byte)_currentByte);
                    _currentByte = 0;
                    _bitsInCurrentByte = 0;
                }
            }
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
