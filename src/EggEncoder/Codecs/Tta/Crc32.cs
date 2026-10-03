namespace EggEncoder.Codecs.Tta
{
    // The standard reflected CRC-32 (polynomial 0xEDB88320, init 0xFFFFFFFF, final XOR 0xFFFFFFFF) --
    // the same variant used by zlib/PNG/zip/Ethernet, and the one TTA uses for its header, seek-table,
    // and per-frame checksums (confirmed against ffmpeg's tta.c: AV_CRC_32_IEEE_LE table with the
    // standard init/final-XOR). .NET has no built-in CRC32 in the base class library, so this is a
    // small from-scratch table-based implementation rather than adding a dependency for one algorithm.
    internal static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in data)
            {
                crc = Table[(byte)(crc ^ b)] ^ (crc >> 8);
            }

            return crc ^ 0xFFFFFFFFu;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (var i = 0u; i < 256; i++)
            {
                var value = i;
                for (var bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                }

                table[i] = value;
            }

            return table;
        }
    }
}
