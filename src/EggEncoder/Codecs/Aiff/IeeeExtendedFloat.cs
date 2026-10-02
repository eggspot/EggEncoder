namespace EggEncoder.Codecs.Aiff
{
    // AIFF's COMM chunk stores its sample rate as an 80-bit ("extended precision") IEEE 754 float: 1 sign
    // bit, 15 exponent bits (bias 16383), and 64 *explicit* mantissa bits -- unlike the 32/64-bit IEEE
    // formats, there's no implicit leading 1, the integer bit is stored. This is the historical Motorola/
    // x87 extended format; .NET has no built-in conversion for it, so this is a small, from-scratch,
    // round-trip-correct implementation scoped to what a sample rate actually needs (a positive, finite
    // value that fits in a double with room to spare) rather than a fully general IEEE-754-extended
    // library: subnormals and the Infinity/NaN encodings (both vanishingly unlikely for a real sample
    // rate) are rejected rather than handled.
    internal static class IeeeExtendedFloat
    {
        public static double ToDouble(ReadOnlySpan<byte> tenBytes)
        {
            var signAndExponent = (tenBytes[0] << 8) | tenBytes[1];
            var sign = (signAndExponent & 0x8000) != 0;
            var biasedExponent = signAndExponent & 0x7FFF;

            var mantissa = 0UL;
            for (var i = 0; i < 8; i++)
            {
                mantissa = (mantissa << 8) | tenBytes[2 + i];
            }

            if (biasedExponent == 0 && mantissa == 0)
            {
                return 0.0;
            }

            if (biasedExponent == 0)
            {
                throw new NotSupportedException("AIFF COMM chunk's sample rate is a subnormal extended-float value, which this reader doesn't support");
            }

            // Rebase from the extended format's bias (16383) to double's (1023).
            var doubleExponent = biasedExponent - 16383 + 1023;
            if (doubleExponent <= 0 || doubleExponent >= 0x7FF)
            {
                throw new NotSupportedException("AIFF COMM chunk's sample rate is outside the range this reader supports (including the extended format's Infinity/NaN encodings)");
            }

            // Double's 52-bit mantissa is the extended mantissa's top 52 fractional bits, with its
            // explicit leading integer bit (always 1 for a normalized value, at bit 63) dropped --
            // double doesn't store that bit at all, it's implicit.
            var doubleMantissa = (mantissa >> 11) & 0xFFFFFFFFFFFFFUL;
            var bits = ((sign ? 1UL : 0UL) << 63) | ((ulong)doubleExponent << 52) | doubleMantissa;

            return BitConverter.Int64BitsToDouble(unchecked((long)bits));
        }

        public static void FromDouble(double value, Span<byte> tenBytes)
        {
            tenBytes.Clear();
            if (value == 0.0)
            {
                return;
            }

            var bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
            var sign = (bits >> 63) != 0;
            var biasedExponent = (int)((bits >> 52) & 0x7FF);
            var mantissa = bits & 0xFFFFFFFFFFFFFUL;

            var extendedExponent = biasedExponent - 1023 + 16383;
            // Restore the explicit leading integer bit (bit 63) and shift double's 52 fractional bits up
            // to occupy the top of the remaining 63 bits (64 - 1 for the integer bit - 52 = 11-bit shift).
            var extendedMantissa = 0x8000000000000000UL | (mantissa << 11);

            var signAndExponent = (ushort)((sign ? 0x8000 : 0) | extendedExponent);
            tenBytes[0] = (byte)(signAndExponent >> 8);
            tenBytes[1] = (byte)signAndExponent;
            for (var i = 0; i < 8; i++)
            {
                tenBytes[2 + i] = (byte)(extendedMantissa >> (8 * (7 - i)));
            }
        }
    }
}
