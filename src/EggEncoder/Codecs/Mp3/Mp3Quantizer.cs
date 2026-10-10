using EggEncoder.Transform;

namespace EggEncoder.Codecs.Mp3
{
    // Quantizes one granule/channel's 576 alias-reduced MDCT coefficients and Huffman-codes them,
    // per the spec's own (decoder-side, inverted here for encoding) non-uniform power-law
    // quantizer formula -- confirmed via independent published technical sources (not any
    // encoder's source) since this project's own extraction of the ISO/IEC 11172-3 standard text
    // rendered this one formula as an unreadable embedded equation image:
    //   xr(i) = sign(is(i)) * |is(i)|^(4/3) * 2^((global_gain-210)/4)
    // This baseline never uses per-band scalefactors (scalefac_compress=0, i.e. slen1=slen2=0,
    // every scalefactor implicitly 0) or the count1 (quadruples) region -- every nonzero
    // coefficient is coded in the big_values region as pairs, using a single Huffman table for the
    // whole granule (table 15 if every quantized magnitude fits in 0-15 directly, otherwise the
    // smallest escape-capable table (24-31) whose linbits covers the largest magnitude present).
    // Simpler than per-region table selection or count1 exploitation, matching this project's own
    // "correctness first, not yet compression-competitive" MVP precedent (see FlacEncoder's own
    // items 2/3 history, and WavPackBlockEncoder's own single-decorrelation-term choice).
    internal static class Mp3Quantizer
    {
        private const int CoefficientCount = 576;
        private const double GainBase = 210.0;

        // Escape-capable tables in ascending linbits order -- the smallest one whose maximum
        // representable escaped value (15 + 2^linbits - 1) covers this granule's own largest
        // quantized magnitude is chosen, so a quiet granule never pays for table 31's own
        // (wasteful, for small values) 13 escape bits per large value.
        private static readonly int[] _escapeTableSelects = [24, 25, 26, 27, 28, 29, 30, 31];

        public readonly record struct Plan(int GlobalGain, int TableSelect, int BigValues, int BitCount, int[] Quantized);

        public static Plan FindPlan(double[] xr, int maxBits)
        {
            if (xr.Length != CoefficientCount)
            {
                throw new ArgumentException($"Expected exactly {CoefficientCount} coefficients", nameof(xr));
            }

            var low = 0;
            var high = 255;
            Plan? best = null;

            while (low <= high)
            {
                var mid = (low + high) / 2;
                var candidate = BuildPlan(xr, mid);

                if (candidate is { } plan && plan.BitCount <= maxBits)
                {
                    best = plan;
                    high = mid - 1;
                }
                else
                {
                    low = mid + 1;
                }
            }

            // No global_gain in [0,255] fit the budget (an extreme edge case -- e.g. maxBits itself
            // is 0) -- fall back to the largest gain (coarsest quantization, fewest bits) even if it
            // still exceeds maxBits, since the caller's own byte budget is a hard ceiling this
            // encoder cannot shrink further without dropping data outright.
            return best ?? BuildPlan(xr, 255) ?? new Plan(255, 24, 0, 0, new int[CoefficientCount]);
        }

        public static void Write(BitWriter writer, Plan plan)
        {
            if (plan.BigValues == 0)
            {
                return;
            }

            var (table, width, linbits) = Mp3HuffmanTables.GetBigValueTable(plan.TableSelect);
            var maxDirect = width - 1;

            for (var pair = 0; pair < plan.BigValues; pair++)
            {
                var x = plan.Quantized[pair * 2];
                var y = plan.Quantized[(pair * 2) + 1];
                WritePair(writer, table, maxDirect, linbits, x, y);
            }
        }

        private static Plan? BuildPlan(double[] xr, int globalGain)
        {
            var scale = Math.Pow(2.0, -(globalGain - GainBase) / 4.0);
            var quantized = new int[CoefficientCount];
            var maxMagnitude = 0;
            var lastNonZero = -1;

            for (var i = 0; i < CoefficientCount; i++)
            {
                var magnitude = Math.Abs(xr[i]) * scale;
                var value = magnitude > 0 ? (int)Math.Round(Math.Pow(magnitude, 0.75), MidpointRounding.AwayFromZero) : 0;
                quantized[i] = xr[i] < 0 ? -value : value;

                if (value != 0)
                {
                    lastNonZero = i;
                    maxMagnitude = Math.Max(maxMagnitude, value);
                }
            }

            if (lastNonZero < 0)
            {
                return new Plan(globalGain, 15, 0, 0, quantized);
            }

            var bigValues = Math.Min(288, (lastNonZero / 2) + 1);
            var tableSelect = SelectTable(maxMagnitude, out var width, out var linbits);
            if (tableSelect < 0)
            {
                // Even the largest escape table (31, max representable 15+8191=8206) can't hold this
                // magnitude -- global_gain needs to go up further; report back as "doesn't fit" so
                // the binary search above keeps increasing it rather than ever writing a value this
                // encoder cannot represent at all.
                return null;
            }

            var (table, _, _) = Mp3HuffmanTables.GetBigValueTable(tableSelect);
            var bitCount = 0;
            var maxDirect = width - 1;
            for (var pair = 0; pair < bigValues; pair++)
            {
                var x = quantized[pair * 2];
                var y = quantized[(pair * 2) + 1];
                bitCount += PairBitCount(table, maxDirect, linbits, x, y);
            }

            return new Plan(globalGain, tableSelect, bigValues, bitCount, quantized);
        }

        private static int SelectTable(int maxMagnitude, out int width, out int linbits)
        {
            if (maxMagnitude <= 15)
            {
                width = 16;
                linbits = 0;
                return 15;
            }

            foreach (var select in _escapeTableSelects)
            {
                var (_, tableWidth, tableLinbits) = Mp3HuffmanTables.GetBigValueTable(select);
                var maxRepresentable = (tableWidth - 1) + (1 << tableLinbits) - 1;
                if (maxMagnitude <= maxRepresentable)
                {
                    width = tableWidth;
                    linbits = tableLinbits;
                    return select;
                }
            }

            width = 0;
            linbits = 0;
            return -1;
        }

        private static int PairBitCount(HuffmanTable table, int maxDirect, int linbits, int x, int y)
        {
            var absX = Math.Abs(x);
            var absY = Math.Abs(y);
            var codeX = Math.Min(absX, maxDirect);
            var codeY = Math.Min(absY, maxDirect);

            var (_, bits) = table.GetCode((codeX * (maxDirect + 1)) + codeY);
            if (absX >= maxDirect && linbits > 0)
            {
                bits += linbits;
            }

            if (x != 0)
            {
                bits++;
            }

            if (absY >= maxDirect && linbits > 0)
            {
                bits += linbits;
            }

            if (y != 0)
            {
                bits++;
            }

            return bits;
        }

        private static void WritePair(BitWriter writer, HuffmanTable table, int maxDirect, int linbits, int x, int y)
        {
            var absX = Math.Abs(x);
            var absY = Math.Abs(y);
            var codeX = Math.Min(absX, maxDirect);
            var codeY = Math.Min(absY, maxDirect);

            var (code, length) = table.GetCode((codeX * (maxDirect + 1)) + codeY);
            writer.WriteBits(code, length);

            if (absX >= maxDirect && linbits > 0)
            {
                writer.WriteBits((uint)(absX - maxDirect), linbits);
            }

            if (x != 0)
            {
                writer.WriteBits(x < 0 ? 1u : 0u, 1);
            }

            if (absY >= maxDirect && linbits > 0)
            {
                writer.WriteBits((uint)(absY - maxDirect), linbits);
            }

            if (y != 0)
            {
                writer.WriteBits(y < 0 ? 1u : 0u, 1);
            }
        }
    }
}
