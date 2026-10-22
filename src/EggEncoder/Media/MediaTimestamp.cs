namespace EggEncoder.Media
{
    /// <summary>
    /// Converts timestamps between track timescales. The only place a timestamp changes scale:
    /// packets keep their source's ticks until a muxer boundary needs another one. See
    /// docs/live-ingest-restream-plan.md item 1.
    /// </summary>
    public static class MediaTimestamp
    {
        /// <summary>
        /// <c>value * toTimescale / fromTimescale</c>, rounded half to even. The intermediate product is
        /// 128-bit, so it never overflows; a result that doesn't fit in a <see cref="long"/> throws
        /// <see cref="OverflowException"/> rather than wrapping.
        /// </summary>
        public static long Rescale(long value, int fromTimescale, int toTimescale)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fromTimescale);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(toTimescale);

            if (fromTimescale == toTimescale)
            {
                return value;
            }

            var product = (Int128)value * toTimescale;
            var quotient = Int128.DivRem(product, fromTimescale);
            var result = quotient.Quotient;

            // The remainder has the dividend's sign; compare its doubled magnitude to the divisor to
            // decide whether to round away from zero (more than half, or exactly half with an odd
            // quotient).
            var doubledRemainder = Int128.Abs(quotient.Remainder) * 2;
            if (doubledRemainder > fromTimescale || (doubledRemainder == fromTimescale && !Int128.IsEvenInteger(result)))
            {
                result += Int128.Sign(product);
            }

            if (result > long.MaxValue || result < long.MinValue)
            {
                throw new OverflowException($"Rescaling {value} from timescale {fromTimescale} to {toTimescale} does not fit in a 64-bit timestamp");
            }

            return (long)result;
        }
    }
}
