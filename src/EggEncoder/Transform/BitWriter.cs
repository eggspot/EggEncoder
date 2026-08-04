namespace EggEncoder.Transform
{
    public sealed class BitWriter
    {
        private readonly List<byte> _buffer = [];
        private byte _currentByte;
        private int _bitsInCurrentByte;

        public int BitPosition => (_buffer.Count * 8) + _bitsInCurrentByte;

        public void WriteBits(uint value, int bitCount)
        {
            for (var i = bitCount - 1; i >= 0; i--)
            {
                var bit = (value >> i) & 1;
                _currentByte = (byte)((_currentByte << 1) | bit);
                _bitsInCurrentByte++;

                if (_bitsInCurrentByte == 8)
                {
                    _buffer.Add(_currentByte);
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

            var remaining = 8 - _bitsInCurrentByte;
            WriteBits(0, remaining);
        }

        public byte[] ToArray()
        {
            var result = new byte[_buffer.Count + (_bitsInCurrentByte > 0 ? 1 : 0)];
            _buffer.CopyTo(result);

            if (_bitsInCurrentByte > 0)
            {
                result[_buffer.Count] = (byte)(_currentByte << (8 - _bitsInCurrentByte));
            }

            return result;
        }
    }
}
