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

            var value = 0u;

            for (var i = 0; i < bitCount; i++)
            {
                var byteIndex = _bitPosition / 8;
                var bitIndexInByte = 7 - (_bitPosition % 8);
                var bit = (_buffer[byteIndex] >> bitIndexInByte) & 1;
                value = (value << 1) | (uint)bit;
                _bitPosition++;
            }

            return value;
        }

        public uint PeekBits(int bitCount)
        {
            var savedPosition = _bitPosition;
            var value = ReadBits(bitCount);
            _bitPosition = savedPosition;

            return value;
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
    }
}
