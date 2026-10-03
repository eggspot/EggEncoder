namespace EggEncoder.Codecs.Opus
{
    // Ogg's own CRC-32 variant -- confirmed against a real ffmpeg-produced .opus file's actual page
    // checksum (not just the written spec): direct (non-reflected, MSB-first) algorithm, generator
    // polynomial 0x04C11DB7, initial value 0, no final XOR. This is a genuinely different algorithm
    // from the reflected/0xEDB88320 zlib-style CRC-32 this codebase already has in
    // EggEncoder.Codecs.Tta.Crc32 (TTA/PNG/zip/Ethernet's variant) -- the two are not
    // interchangeable despite both being "CRC-32", hence the distinct name and namespace here.
    // Computed over the entire page (header, segment table, and packet data) with the page's own
    // 4-byte checksum field zeroed out during the calculation.
    internal static class OggCrc32
    {
        private static readonly uint[] Table = BuildTable();

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            var crc = 0u;
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
