namespace EggEncoder.Codecs.WavPack
{
    // Writes WavPack's own bitstream convention: bits packed LSB-first within each byte, the exact
    // inverse of WavPackBitReader. Buffers into a growable byte list, flushing (zero-padding) any
    // partial trailing byte on Finish().
    internal sealed class WavPackBitWriter
    {
        private readonly List<byte> _bytes = [];
        private byte _current;
        private int _bitPosition;

        public void WriteBit(int bit)
        {
            if (bit != 0)
            {
                _current |= (byte)(1 << _bitPosition);
            }

            _bitPosition++;
            if (_bitPosition == 8)
            {
                _bytes.Add(_current);
                _current = 0;
                _bitPosition = 0;
            }
        }

        public void WriteBits(uint value, int count)
        {
            for (var i = 0; i < count; i++)
            {
                WriteBit((int)((value >> i) & 1));
            }
        }

        // Writes `count` one-bits followed by a terminating zero-bit -- the capped (max 33, no
        // terminator at the cap) unary code WavPackBitReader's own ReadUnary0To33 reads back.
        public void WriteUnary0To33(int count)
        {
            for (var i = 0; i < count; i++)
            {
                WriteBit(1);
            }

            if (count < 33)
            {
                WriteBit(0);
            }
        }

        public byte[] Finish()
        {
            if (_bitPosition > 0)
            {
                _bytes.Add(_current);
                _current = 0;
                _bitPosition = 0;
            }

            return [.. _bytes];
        }
    }
}
