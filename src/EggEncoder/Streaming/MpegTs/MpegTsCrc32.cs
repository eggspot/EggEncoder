namespace EggEncoder.Streaming.MpegTs
{
    // MPEG-2 Systems' own PSI-table CRC-32 (ISO/IEC 13818-1 Annex B): direct (non-reflected,
    // MSB-first) algorithm, generator polynomial 0x04C11DB7, initial value 0xFFFFFFFF, no final
    // XOR. Same polynomial and bit direction as Codecs.Opus.OggCrc32, but a different initial
    // value (0xFFFFFFFF here vs. 0 there) -- genuinely not interchangeable despite sharing both the
    // polynomial and the "direct CRC-32" shape, so this gets its own small implementation rather
    // than reusing that one with the wrong seed.
    internal static class MpegTsCrc32
    {
        private static readonly uint[] Table = BuildTable();

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in data)
            {
                crc = (crc << 8) ^ Table[(byte)((crc >> 24) ^ b)];
            }

            return crc;
        }

        private static uint[] BuildTable()
        {
            const uint polynomial = 0x04C11DB7u;
            var table = new uint[256];

            for (var i = 0u; i < 256; i++)
            {
                var value = i << 24;
                for (var bit = 0; bit < 8; bit++)
                {
                    value = (value & 0x80000000u) != 0 ? (value << 1) ^ polynomial : value << 1;
                }

                table[i] = value;
            }

            return table;
        }
    }
}
