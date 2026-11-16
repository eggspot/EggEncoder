using System.Numerics;
using EggEncoder.Transform;

namespace EggEncoder.Codecs.Mp3
{
    internal static class Mp3Quantizer
    {
        private const int CoefficientCount = 576;
        private const double GainBase = 210.0;
        private const int GainCount = 256;

        private static readonly int[] _escapeTableSelects = [24, 25, 26, 27, 28, 29, 30, 31];
        private static readonly int[][] _pairBits = BuildPairBits();
        private static readonly int _largestCodableMagnitude = LargestCodableMagnitude();

        public readonly record struct Plan(int GlobalGain, int TableSelect, int BigValues, int BitCount, int[] Quantized);

        public static Plan FindPlan(double[] xr, int maxBits) => FindPlan(xr, maxBits, null);

        public static Plan FindPlan(double[] xr, int maxBits, PlanCache? cache)
        {
            cache ??= new PlanCache();
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
                var candidate = cache.Get(xr, mid, maxBits);

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

            return best ?? cache.Get(xr, 255, int.MaxValue) ?? new Plan(255, 24, 0, 0, new int[CoefficientCount]);
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

        internal sealed class PlanCache
        {
            private readonly Plan?[] _plans = new Plan?[GainCount];
            private readonly bool[] _built = new bool[GainCount];
            private readonly int[] _countedUpTo = new int[GainCount];
            private readonly double[] _companded = new double[CoefficientCount];
            private readonly double[] _largestFrom = new double[CoefficientCount];
            private double[]? _coefficients;

            public Plan? Get(double[] xr, int globalGain, int bitLimit)
            {
                if (!ReferenceEquals(xr, _coefficients))
                {
                    Array.Clear(_built);
                    _coefficients = xr;
                    for (var i = 0; i < CoefficientCount; i++)
                    {
                        _companded[i] = Math.Pow(Math.Abs(xr[i]), 0.75);
                    }

                    var largest = 0.0;
                    for (var i = CoefficientCount - 1; i >= 0; i--)
                    {
                        largest = Math.Max(largest, _companded[i]);
                        _largestFrom[i] = largest;
                    }
                }

                if (!_built[globalGain] || bitLimit > _countedUpTo[globalGain])
                {
                    var plan = BuildPlan(xr, _companded, _largestFrom, globalGain, bitLimit);
                    _plans[globalGain] = plan;
                    _built[globalGain] = true;
                    _countedUpTo[globalGain] = plan is { } built && built.BitCount > bitLimit ? bitLimit : int.MaxValue;
                }

                return _plans[globalGain];
            }
        }

        private static Plan? BuildPlan(double[] xr, double[] companded, double[] largestFrom, int globalGain, int bitLimit)
        {
            var scale = Math.Pow(2.0, -(globalGain - GainBase) * 3.0 / 16.0);
            if (largestFrom[0] * scale >= _largestCodableMagnitude + 0.5)
            {
                return null;
            }

            var quantized = new int[CoefficientCount];
            var maxMagnitude = 0;
            var lastNonZero = -1;
            var end = CoefficientCount;
            while (end > 0 && largestFrom[end - 1] * scale < 0.5)
            {
                end--;
            }

            var i = 0;
            if (Vector.IsHardwareAccelerated)
            {
                var lanes = Vector<double>.Count;
                var scales = new Vector<double>(scale);
                var half = new Vector<double>(0.5);
                var one = Vector<double>.One;
                for (; i + lanes <= end; i += lanes)
                {
                    var magnitude = new Vector<double>(companded, i) * scales;
                    var whole = Vector.Floor(magnitude);
                    var rounded = Vector.ConditionalSelect(Vector.GreaterThanOrEqual(magnitude - whole, half), whole + one, whole);
                    var values = Vector.ConvertToInt64(rounded);
                    for (var lane = 0; lane < lanes; lane++)
                    {
                        var value = (int)values[lane];
                        quantized[i + lane] = xr[i + lane] < 0 ? -value : value;
                        maxMagnitude = Math.Max(maxMagnitude, value);
                    }
                }
            }

            for (; i < end; i++)
            {
                var value = Quantize(companded[i] * scale);
                quantized[i] = xr[i] < 0 ? -value : value;
                maxMagnitude = Math.Max(maxMagnitude, value);
            }

            for (var j = end - 1; j >= 0; j--)
            {
                if (quantized[j] != 0)
                {
                    lastNonZero = j;
                    break;
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
                return null;
            }

            var pairBits = _pairBits[tableSelect];
            var bitCount = 0;
            var maxDirect = width - 1;
            for (var pair = 0; pair < bigValues; pair++)
            {
                var absX = Math.Abs(quantized[pair * 2]);
                var absY = Math.Abs(quantized[(pair * 2) + 1]);
                bitCount += pairBits[(Math.Min(absX, maxDirect) * width) + Math.Min(absY, maxDirect)];
                if (linbits > 0)
                {
                    bitCount += (absX >= maxDirect ? linbits : 0) + (absY >= maxDirect ? linbits : 0);
                }

                if (bitCount > bitLimit)
                {
                    break;
                }
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

        private static int Quantize(double magnitude)
        {
            var whole = Math.Floor(magnitude);
            return (int)whole + (magnitude - whole >= 0.5 ? 1 : 0);
        }

        private static int LargestCodableMagnitude()
        {
            var (_, width, linbits) = Mp3HuffmanTables.GetBigValueTable(_escapeTableSelects[^1]);
            return (width - 1) + (1 << linbits) - 1;
        }

        private static int[][] BuildPairBits()
        {
            var pairBits = new int[32][];
            foreach (var select in _escapeTableSelects.Prepend(15))
            {
                var (table, width, _) = Mp3HuffmanTables.GetBigValueTable(select);
                var bits = new int[width * width];
                for (var x = 0; x < width; x++)
                {
                    for (var y = 0; y < width; y++)
                    {
                        bits[(x * width) + y] = table.GetCode((x * width) + y).Length + (x != 0 ? 1 : 0) + (y != 0 ? 1 : 0);
                    }
                }

                pairBits[select] = bits;
            }

            return pairBits;
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
