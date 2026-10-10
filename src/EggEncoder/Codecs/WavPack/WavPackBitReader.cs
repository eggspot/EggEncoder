namespace EggEncoder.Codecs.WavPack
{
    // Reads WavPack's own bitstream convention: bits are packed LSB-first within each byte (the
    // opposite of FlacFrameDecoder/AacFrameDecoder's MSB-first Transform.BitReader), and bytes are
    // consumed in file order.
    internal sealed class WavPackBitReader
    {
        private readonly byte[] _data;
        private readonly int _end;
        private int _bytePosition;
        private int _bitPosition;

        public WavPackBitReader(byte[] data, int start, int end)
        {
            _data = data;
            _bytePosition = start;
            _end = end;
        }

        public bool HasMoreData => _bytePosition < _end;

        public int ReadBit()
        {
            if (_bytePosition >= _end)
            {
                throw new EndOfStreamException("Attempted to read past the end of a WavPack bitstream sub-block.");
            }

            var bit = (_data[_bytePosition] >> _bitPosition) & 1;
            _bitPosition++;
            if (_bitPosition == 8)
            {
                _bitPosition = 0;
                _bytePosition++;
            }

            return bit;
        }

        public uint ReadBits(int count)
        {
            var value = 0u;
            for (var i = 0; i < count; i++)
            {
                value |= (uint)ReadBit() << i;
            }

            return value;
        }
    }
}
