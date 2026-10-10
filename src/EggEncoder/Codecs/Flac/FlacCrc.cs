namespace EggEncoder.Codecs.Flac
{
    // The two CRCs RFC 9639 defines for a FLAC frame (section 9.1.6's header CRC-8, section 9.3's
    // frame-footer CRC-16), shared between FlacFrameDecoder (verifying them) and FlacFrameEncoder
    // (computing them) so both sides agree on the exact same table-driven implementation.
    internal static class FlacCrc
    {
        private static readonly byte[] _crc8Table = BuildCrc8Table();
        private static readonly ushort[] _crc16Table = BuildCrc16Table();

        public static byte Crc8(byte[] data, int start, int end)
        {
            byte crc = 0;
            for (var i = start; i < end; i++)
            {
                crc = _crc8Table[crc ^ data[i]];
            }

            return crc;
        }

        public static ushort Crc16(byte[] data, int start, int end)
        {
            ushort crc = 0;
            for (var i = start; i < end; i++)
            {
                crc = (ushort)((crc << 8) ^ _crc16Table[(crc >> 8) ^ data[i]]);
            }

            return crc;
        }

        private static byte[] BuildCrc8Table()
        {
            // Polynomial x^8 + x^2 + x^1 + 1 (0x07), initial value 0 -- RFC 9639 section 9.1.6.
            var table = new byte[256];
            for (var i = 0; i < 256; i++)
            {
                var crc = i;
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 0x80) != 0 ? ((crc << 1) ^ 0x07) & 0xFF : (crc << 1) & 0xFF;
                }

                table[i] = (byte)crc;
            }

            return table;
        }

        private static ushort[] BuildCrc16Table()
        {
            // Polynomial x^16 + x^15 + x^2 + 1 (0x8005), initial value 0 -- RFC 9639 section 9.3.
            var table = new ushort[256];
            for (var i = 0; i < 256; i++)
            {
                var crc = i << 8;
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x8005 : crc << 1;
                }

                table[i] = (ushort)crc;
            }

            return table;
        }
    }
}
