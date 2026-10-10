namespace EggEncoder.Codecs.WavPack
{
    // WavPack stores several metadata values (decorrelation history samples, entropy coder
    // medians) in a compact 16-bit "log2 fixed-point" form -- roughly sign + 8-bit binary exponent
    // + 8-bit mantissa -- rather than as plain linear integers, presumably so a wide dynamic range
    // of magnitudes can be represented in a fixed 16 bits. This expands that form back to a linear
    // integer.
    //
    // The mantissa table (which value byte 0-255 maps to) is simply the fractional part of
    // 2^(byte/256) scaled by 256 and rounded -- the standard "log2 mantissa" lookup table used to
    // compute binary exponentials via an integer table lookup plus a shift, rather than a direct
    // derivation of WavPack's own specific encoder; computed here from that definition rather than
    // transcribed from any particular implementation's precomputed table.
    internal static class WavPackExp2
    {
        private static readonly int[] _mantissaTable = BuildMantissaTable();

        public static int Expand(short value)
        {
            var sign = value < 0;
            var magnitude = sign ? -value : value;

            var exponent = magnitude >> 8;
            if (exponent > 31)
            {
                return sign ? int.MinValue : int.MaxValue;
            }

            // The table only covers the fractional part of 2^x for x in [0,1); OR-ing in the
            // implicit leading 1 reinstates the "1." that a normalized mantissa always has, giving
            // a 9-bit fixed-point value in [256,511] representing [1.0, 2.0) scaled by 256.
            var mantissa = _mantissaTable[magnitude & 0xFF] | 0x100;
            var result = exponent > 9 ? mantissa << (exponent - 9) : mantissa >> (9 - exponent);

            return sign ? -result : result;
        }

        private static int[] BuildMantissaTable()
        {
            var table = new int[256];
            for (var i = 0; i < 256; i++)
            {
                table[i] = (int)Math.Round(256 * (Math.Pow(2, i / 256.0) - 1), MidpointRounding.AwayFromZero);
            }

            return table;
        }
    }
}
