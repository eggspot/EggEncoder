using System.Numerics;
using EggEncoder.Transform;

namespace EggEncoder.Codecs.Mp3
{
    internal static class Mp3Quantizer
    {
        private const int CoefficientCount = 576;
        private const double GainBase = 210.0;
        private const int GainCount = 256;

        private const int BandCount = 22;
        private const int MaxRegion0Count = 15;
        private const int MaxRegion1Count = 7;
        private const int SymbolMagnitudeLimit = 15;
        private const int SymbolCount = (SymbolMagnitudeLimit + 1) * (SymbolMagnitudeLimit + 1);
        private const double ExactIntegerBias = 4503599627370496.0;
        internal const int MaxSegmentBits = 4095;
        internal const int MaxGlobalGain = 255;

        private static readonly int[] _tableSelects = [1, 2, 3, 5, 6, 7, 8, 9, 10, 11, 12, 13, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31];
        private static readonly int[] _lowEscapeTableSelects = [16, 17, 18, 19, 20, 21, 22, 23];
        private static readonly int[] _escapeTableSelects = [24, 25, 26, 27, 28, 29, 30, 31];
        private static readonly int[] _widths = new int[32];
        private static readonly int[] _linbits = new int[32];
        private static readonly int[][] _pairSymbolBits = BuildPairSymbolBits();
        private static readonly int[][] _candidates = BuildCandidates();
        private static readonly int[][] _escapeCandidates = BuildEscapeCandidates();
        private static readonly int[] _count1BitsA = BuildCount1Bits();
        private static readonly int _largestCodableMagnitude = LargestCodableMagnitude();

        [ThreadStatic]
        private static DivisionScratch? _divisionScratch;

        [ThreadStatic]
        private static int[]? _clampedMagnitudes;

        public readonly record struct Division(int Region0Count, int Region1Count, int Table0, int Table1, int Table2);

        public readonly record struct Plan(int GlobalGain, int BigValues, int BitCount, int[] Quantized, byte[] PairSymbols, Division Division, int Count1Table, int Count1End);

        public static void Write(BitWriter writer, Plan plan, int[] bandStarts)
        {
            var bigEnd = plan.BigValues * 2;
            var (region0End, region1End) = RegionEnds(plan.Division, bigEnd, bandStarts);
            WriteRegion(writer, plan.Quantized, 0, region0End, plan.Division.Table0);
            WriteRegion(writer, plan.Quantized, region0End, region1End, plan.Division.Table1);
            WriteRegion(writer, plan.Quantized, region1End, bigEnd, plan.Division.Table2);
            var quads = plan.Count1Table == 0 ? Mp3HuffmanTables.TableA : Mp3HuffmanTables.TableB;
            for (var i = bigEnd; i < plan.Count1End; i += 4)
            {
                var pattern = 0;
                for (var j = 0; j < 4; j++)
                {
                    pattern = (pattern << 1) | (plan.Quantized[i + j] != 0 ? 1 : 0);
                }

                var (code, length) = quads.GetCode(pattern);
                for (var j = 0; j < 4; j++)
                {
                    var value = plan.Quantized[i + j];
                    var nonZero = (int)((uint)(value | -value) >> 31);
                    code = (code << nonZero) | (uint)((value >>> 31) & nonZero);
                    length += nonZero;
                }

                writer.WriteBits(code, length);
            }
        }

        internal sealed class PlanCache
        {
            private readonly Plan?[] _plans = new Plan?[GainCount];
            private readonly bool[] _built = new bool[GainCount];
            private readonly int[] _countedUpTo = new int[GainCount];
            private readonly Plan?[] _quantized = new Plan?[GainCount];
            private readonly bool[] _quantizedBuilt = new bool[GainCount];
            private readonly double[] _companded = new double[CoefficientCount];
            private readonly double[] _largestFrom = new double[CoefficientCount];
            private readonly int[] _signs = new int[CoefficientCount];
            private readonly int[] _builtGains = new int[GainCount * 2];
            private int _builtGainCount;
            private double[]? _coefficients;

            public Plan? GetQuantized(double[] xr, int globalGain, int[] bandStarts)
            {
                Prepare(xr);
                if (_built[globalGain] && _countedUpTo[globalGain] == int.MaxValue)
                {
                    return _plans[globalGain] is { } counted ? counted with { BitCount = Count1Cost(counted.Quantized, counted.BigValues * 2, counted.Count1End).Bits, Division = default } : null;
                }

                if (!_quantizedBuilt[globalGain])
                {
                    _builtGains[_builtGainCount++] = globalGain;
                    _quantized[globalGain] = BuildPlan(xr, _companded, _largestFrom, _signs, globalGain, int.MaxValue, bandStarts, costRegions: false);
                    _quantizedBuilt[globalGain] = true;
                }

                return _quantized[globalGain];
            }

            public Plan? Get(double[] xr, int globalGain, int bitLimit, int[] bandStarts)
            {
                Prepare(xr);
                if (!_built[globalGain] || bitLimit > _countedUpTo[globalGain])
                {
                    if (!_built[globalGain])
                    {
                        _builtGains[_builtGainCount++] = globalGain;
                    }

                    var plan = BuildPlan(xr, _companded, _largestFrom, _signs, globalGain, bitLimit, bandStarts, costRegions: true);
                    _plans[globalGain] = plan;
                    _built[globalGain] = true;
                    _countedUpTo[globalGain] = plan is { } built && built.BitCount > bitLimit ? bitLimit : int.MaxValue;
                }

                return _plans[globalGain];
            }

            private void Prepare(double[] xr)
            {
                if (!ReferenceEquals(xr, _coefficients))
                {
                    for (var g = 0; g < _builtGainCount; g++)
                    {
                        var gain = _builtGains[g];
                        _plans[gain] = null;
                        _quantized[gain] = null;
                        _built[gain] = false;
                        _quantizedBuilt[gain] = false;
                    }

                    _builtGainCount = 0;
                    _coefficients = xr;
                    Compand(xr, _companded);
                    NegativeMask(xr, _signs);

                    var largest = 0.0;
                    for (var i = CoefficientCount - 1; i >= 0; i--)
                    {
                        largest = Math.Max(largest, _companded[i]);
                        _largestFrom[i] = largest;
                    }
                }
            }
        }

        private static void Compand(double[] xr, double[] companded)
        {
            var i = 0;
            if (Vector.IsHardwareAccelerated)
            {
                for (; i + Vector<double>.Count <= CoefficientCount; i += Vector<double>.Count)
                {
                    var magnitude = Vector.Abs(new Vector<double>(xr, i));
                    Vector.SquareRoot(magnitude * Vector.SquareRoot(magnitude)).CopyTo(companded, i);
                }
            }

            for (; i < CoefficientCount; i++)
            {
                var magnitude = Math.Abs(xr[i]);
                companded[i] = Math.Sqrt(magnitude * Math.Sqrt(magnitude));
            }
        }

        internal static Plan EmptyPlan(int globalGain) => new(globalGain, 0, 0, new int[CoefficientCount], [], default, 0, 0);

        private static Plan? BuildPlan(double[] xr, double[] companded, double[] largestFrom, int[] signs, int globalGain, int bitLimit, int[] bandStarts, bool costRegions)
        {
            var scale = Math.Pow(2.0, -(globalGain - GainBase) * 3.0 / 16.0);
            if (largestFrom[0] * scale >= _largestCodableMagnitude + 0.5)
            {
                return null;
            }

            var quantized = new int[CoefficientCount];
            var end = FirstRoundingToZero(largestFrom, scale);
            var i = 0;
            if (Vector.IsHardwareAccelerated)
            {
                var lanes = Vector<double>.Count;
                var scales = new Vector<double>(scale);
                var half = new Vector<double>(0.5);
                var bias = new Vector<double>(ExactIntegerBias);
                var biasBits = Vector.AsVectorInt64(bias);
                for (; i + (2 * lanes) <= end; i += 2 * lanes)
                {
                    var low = Vector.AsVectorInt64(Vector.Floor((new Vector<double>(companded, i) * scales) + half) + bias) - biasBits;
                    var high = Vector.AsVectorInt64(Vector.Floor((new Vector<double>(companded, i + lanes) * scales) + half) + bias) - biasBits;
                    var sign = new Vector<int>(signs, i);
                    ((Vector.Narrow(low, high) ^ sign) - sign).CopyTo(quantized, i);
                }
            }

            for (; i < end; i++)
            {
                var value = Quantize(companded[i] * scale);
                quantized[i] = xr[i] < 0 ? -value : value;
            }

            var zeroStart = end;
            while (zeroStart > 0 && quantized[zeroStart - 1] == 0)
            {
                zeroStart--;
            }

            if (zeroStart == 0)
            {
                return EmptyPlan(globalGain);
            }

            var count1End = (zeroStart + 1) & ~1;
            var bigEnd = count1End;
            while (bigEnd >= 4 && Math.Abs(quantized[bigEnd - 1]) <= 1 && Math.Abs(quantized[bigEnd - 2]) <= 1 && Math.Abs(quantized[bigEnd - 3]) <= 1 && Math.Abs(quantized[bigEnd - 4]) <= 1)
            {
                bigEnd -= 4;
            }

            var (count1Table, count1Bits) = Count1Cost(quantized, bigEnd, count1End);
            var symbols = PairSymbols(quantized, bigEnd);
            if (!costRegions)
            {
                return new Plan(globalGain, bigEnd / 2, count1Bits, quantized, symbols, default, count1Table, count1End);
            }

            var (division, divisionBits) = ThirdsDivision(quantized, symbols, bigEnd, bandStarts, bitLimit - count1Bits);
            return new Plan(globalGain, bigEnd / 2, count1Bits + divisionBits, quantized, symbols, division, count1Table, count1End);
        }

        private static int FirstRoundingToZero(double[] largestFrom, double scale)
        {
            var low = 0;
            var high = CoefficientCount;
            while (low < high)
            {
                var middle = (low + high) >> 1;
                if (largestFrom[middle] * scale < 0.5)
                {
                    high = middle;
                }
                else
                {
                    low = middle + 1;
                }
            }

            return low;
        }

        private static byte[] PairSymbols(int[] quantized, int bigEnd)
        {
            var symbols = new byte[bigEnd / 2];
            var clamped = _clampedMagnitudes ??= new int[CoefficientCount];
            var i = 0;
            if (Vector.IsHardwareAccelerated)
            {
                var limit = new Vector<int>(SymbolMagnitudeLimit);
                for (; i + Vector<int>.Count <= bigEnd; i += Vector<int>.Count)
                {
                    Vector.Min(Vector.Abs(new Vector<int>(quantized, i)), limit).CopyTo(clamped, i);
                }
            }

            for (; i < bigEnd; i++)
            {
                clamped[i] = Math.Min(Math.Abs(quantized[i]), SymbolMagnitudeLimit);
            }

            for (var pair = 0; pair < symbols.Length; pair++)
            {
                symbols[pair] = (byte)((clamped[pair * 2] * (SymbolMagnitudeLimit + 1)) + clamped[(pair * 2) + 1]);
            }

            return symbols;
        }

        private static int LargestMagnitude(int[] quantized, int start, int end)
        {
            var largest = 0;
            var i = start;
            if (Vector.IsHardwareAccelerated && end - start >= Vector<int>.Count)
            {
                var largestLanes = Vector<int>.Zero;
                for (; i + Vector<int>.Count <= end; i += Vector<int>.Count)
                {
                    largestLanes = Vector.Max(largestLanes, Vector.Abs(new Vector<int>(quantized, i)));
                }

                for (var lane = 0; lane < Vector<int>.Count; lane++)
                {
                    largest = Math.Max(largest, largestLanes[lane]);
                }
            }

            for (; i < end; i++)
            {
                largest = Math.Max(largest, Math.Abs(quantized[i]));
            }

            return largest;
        }

        private static void NegativeMask(double[] xr, int[] signs)
        {
            var i = 0;
            if (Vector.IsHardwareAccelerated)
            {
                var lanes = Vector<double>.Count;
                for (; i + (2 * lanes) <= CoefficientCount; i += 2 * lanes)
                {
                    var low = Vector.LessThan(new Vector<double>(xr, i), Vector<double>.Zero);
                    var high = Vector.LessThan(new Vector<double>(xr, i + lanes), Vector<double>.Zero);
                    Vector.Narrow(low, high).CopyTo(signs, i);
                }
            }

            for (; i < CoefficientCount; i++)
            {
                signs[i] = xr[i] < 0 ? -1 : 0;
            }
        }

        internal static Plan WithBestDivision(Plan plan, int[] bandStarts)
        {
            var (division, bits) = BestDivision(plan.Quantized, plan.PairSymbols, plan.BigValues * 2, bandStarts);
            var count1Bits = Count1Cost(plan.Quantized, plan.BigValues * 2, plan.Count1End).Bits;
            return bits >= plan.BitCount - count1Bits ? plan : plan with { Division = division, BitCount = count1Bits + bits };
        }

        internal static Plan AddRegions(Plan quantizedOnly, int region0, int region1, int[] bandStarts)
        {
            var (division, bits) = DivisionFor(quantizedOnly.Quantized, quantizedOnly.PairSymbols, quantizedOnly.BigValues * 2, bandStarts, region0, region1, int.MaxValue);
            return quantizedOnly with { Division = division, BitCount = quantizedOnly.BitCount + bits };
        }

        private static (Division Division, int Bits) ThirdsDivision(int[] quantized, byte[] symbols, int bigEnd, int[] bandStarts, int bitLimit)
        {
            if (bigEnd == 0)
            {
                return (default, 0);
            }

            var bands = 0;
            while (bands < BandCount && bandStarts[bands] < bigEnd)
            {
                bands++;
            }

            var region0 = Math.Clamp((bands / 3) - 1, 0, MaxRegion0Count);
            var region1 = Math.Clamp((bands / 3) - 1, 0, MaxRegion1Count);
            return DivisionFor(quantized, symbols, bigEnd, bandStarts, region0, region1, bitLimit);
        }

        private static (Division Division, int Bits) DivisionFor(int[] quantized, byte[] symbols, int bigEnd, int[] bandStarts, int region0, int region1, int bitLimit)
        {
            if (bigEnd == 0)
            {
                return (default, 0);
            }

            var (region0End, region1End) = RegionEnds(new Division(region0, region1, 0, 0, 0), bigEnd, bandStarts);
            var first = CheapestRegion(quantized, symbols, 0, region0End, bitLimit);
            var bits = first.Bits;
            var second = bits <= bitLimit ? CheapestRegion(quantized, symbols, region0End, region1End, bitLimit - bits) : (Table: 0, Bits: 0);
            bits += second.Bits;
            var third = bits <= bitLimit ? CheapestRegion(quantized, symbols, region1End, bigEnd, bitLimit - bits) : (Table: 0, Bits: 0);
            bits += third.Bits;
            return (new Division(region0, region1, first.Table, second.Table, third.Table), bits);
        }

        private static (int Table, int Bits) CheapestRegion(int[] quantized, byte[] symbols, int start, int end, int bitLimit)
        {
            var largest = LargestMagnitude(quantized, start, end);
            var best = (Table: 0, Bits: int.MaxValue);
            foreach (var table in Candidates(largest))
            {
                var bits = RegionCost(symbols, start, end, table, Math.Min(bitLimit, best.Bits));
                if (bits < best.Bits)
                {
                    best = (table, bits);
                }
            }

            return best;
        }

        private static int RegionCost(byte[] symbols, int start, int end, int table, int bitLimit)
        {
            if (table == 0 || start >= end)
            {
                return 0;
            }

            var symbolBits = _pairSymbolBits[table];
            var bits = 0;
            for (var pair = start / 2; pair < end / 2; pair++)
            {
                bits += symbolBits[symbols[pair]];
                if (bits > bitLimit)
                {
                    break;
                }
            }

            return bits;
        }

        private static (int Table, int Bits) Count1Cost(int[] quantized, int start, int end)
        {
            var bitsA = 0;
            var bitsB = 0;
            for (var i = start; i < end; i += 4)
            {
                var pattern = 0;
                var signs = 0;
                for (var j = 0; j < 4; j++)
                {
                    var nonZero = quantized[i + j] != 0 ? 1 : 0;
                    pattern = (pattern << 1) | nonZero;
                    signs += nonZero;
                }

                bitsA += _count1BitsA[pattern] + signs;
                bitsB += 4 + signs;
            }

            return bitsB < bitsA ? (1, bitsB) : (0, bitsA);
        }

        private static (int Region0End, int Region1End) RegionEnds(Division division, int bigEnd, int[] bandStarts)
        {
            var region0End = Math.Min(bandStarts[Math.Min(division.Region0Count + 1, BandCount)], bigEnd);
            var region1End = Math.Min(bandStarts[Math.Min(division.Region0Count + division.Region1Count + 2, BandCount)], bigEnd);
            return (region0End, Math.Max(region0End, region1End));
        }

        private static (Division Division, int Bits) BestDivision(int[] quantized, byte[] symbols, int bigEnd, int[] bandStarts)
        {
            if (bigEnd == 0)
            {
                return (default, 0);
            }

            var scratch = _divisionScratch ??= new DivisionScratch();
            scratch.Begin(quantized, symbols, bigEnd, bandStarts);
            var bands = scratch.Bands;
            var bestDivision = default(Division);
            var bestBits = int.MaxValue;
            for (var region0 = 0; region0 <= MaxRegion0Count; region0++)
            {
                var region0Bands = Math.Min(region0 + 1, bands);
                var first = scratch.Region(0, region0Bands);
                if (first.Bits >= bestBits)
                {
                    continue;
                }

                for (var region1 = 0; region1 <= MaxRegion1Count; region1++)
                {
                    var region1Bands = Math.Min(region0 + region1 + 2, bands);
                    var second = scratch.Region(region0Bands, region1Bands);
                    if (first.Bits + second.Bits < bestBits)
                    {
                        var third = scratch.Region(region1Bands, bands);
                        var bits = first.Bits + second.Bits + third.Bits;
                        if (bits < bestBits)
                        {
                            bestBits = bits;
                            bestDivision = new Division(region0, region1, first.Table, second.Table, third.Table);
                        }
                    }

                    if (region1Bands == bands)
                    {
                        break;
                    }
                }

                if (region0Bands == bands)
                {
                    break;
                }
            }

            return (bestDivision, bestBits);
        }

        private sealed class DivisionScratch
        {
            private readonly int[][] _prefixes = new int[32][];
            private readonly int[] _prefixStamps = new int[32];
            private readonly int[] _rangeLargest = new int[(BandCount + 1) * (BandCount + 1)];
            private readonly int[] _bandEnds = new int[BandCount + 1];
            private readonly int[] _bandLargest = new int[BandCount];
            private readonly int[] _regionStamps = new int[(BandCount + 1) * (BandCount + 1)];
            private readonly (int Table, int Bits)[] _regions = new (int Table, int Bits)[(BandCount + 1) * (BandCount + 1)];
            private byte[] _symbols = [];
            private int _pairs;
            private int _stamp;

            public int Bands { get; private set; }

            public void Begin(int[] quantized, byte[] symbols, int bigEnd, int[] bandStarts)
            {
                _stamp++;
                _symbols = symbols;
                _pairs = bigEnd / 2;
                var bands = 0;
                while (bands < BandCount && bandStarts[bands] < bigEnd)
                {
                    bands++;
                }

                Bands = bands;
                _bandEnds[0] = 0;
                for (var band = 0; band < bands; band++)
                {
                    _bandEnds[band + 1] = Math.Min(bandStarts[band + 1], bigEnd);
                }

                for (var band = 0; band < bands; band++)
                {
                    _bandLargest[band] = LargestMagnitude(quantized, _bandEnds[band], _bandEnds[band + 1]);
                }

                for (var first = 0; first < bands; first++)
                {
                    var largest = 0;
                    for (var band = first; band < bands; band++)
                    {
                        largest = Math.Max(largest, _bandLargest[band]);
                        _rangeLargest[(first * (BandCount + 1)) + band + 1] = largest;
                    }
                }
            }

            public (int Table, int Bits) Region(int firstBand, int endBand)
            {
                if (firstBand >= endBand)
                {
                    return (0, 0);
                }

                var key = (firstBand * (BandCount + 1)) + endBand;
                if (_regionStamps[key] == _stamp)
                {
                    return _regions[key];
                }

                var largest = _rangeLargest[key];
                var startPair = _bandEnds[firstBand] / 2;
                var endPair = _bandEnds[endBand] / 2;
                var best = (Table: 0, Bits: int.MaxValue);
                foreach (var table in Candidates(largest))
                {
                    var bits = 0;
                    if (table != 0)
                    {
                        var prefix = Prefix(table);
                        bits = prefix[endPair] - prefix[startPair];
                    }

                    if (bits < best.Bits)
                    {
                        best = (table, bits);
                    }
                }

                _regionStamps[key] = _stamp;
                _regions[key] = best;
                return best;
            }

            private int[] Prefix(int table)
            {
                var prefix = _prefixes[table] ??= new int[(CoefficientCount / 2) + 1];
                if (_prefixStamps[table] == _stamp)
                {
                    return prefix;
                }

                var symbolBits = _pairSymbolBits[table];
                var symbols = _symbols;
                var bits = 0;
                for (var pair = 0; pair < _pairs; pair++)
                {
                    bits += symbolBits[symbols[pair]];
                    prefix[pair + 1] = bits;
                }

                _prefixStamps[table] = _stamp;
                return prefix;
            }
        }

        private static void WriteRegion(BitWriter writer, int[] quantized, int start, int end, int table)
        {
            if (table == 0)
            {
                return;
            }

            var (huffman, width, linbits) = Mp3HuffmanTables.GetBigValueTable(table);
            var maxDirect = width - 1;
            for (var i = start; i < end; i += 2)
            {
                WritePair(writer, huffman, maxDirect, linbits, quantized[i], quantized[i + 1]);
            }
        }

        private static int[] Candidates(int largest) =>
            largest <= 15 ? _candidates[largest] : _escapeCandidates[32 - BitOperations.LeadingZeroCount((uint)(largest - 15))];

        private static int[][] BuildEscapeCandidates()
        {
            var candidates = new int[14][];
            for (var needed = 1; needed <= 13; needed++)
            {
                var low = Array.Find(_lowEscapeTableSelects, table => _linbits[table] >= needed);
                var high = Array.Find(_escapeTableSelects, table => _linbits[table] >= needed);
                candidates[needed] = low == 0 ? [high] : [low, high];
            }

            return candidates;
        }

        private static int[][] BuildCandidates()
        {
            var candidates = new int[16][];
            candidates[0] = [0];
            for (var largest = 1; largest <= 15; largest++)
            {
                var width = _tableSelects.Where(table => _linbits[table] == 0 && _widths[table] > largest).Min(table => _widths[table]);
                candidates[largest] = _tableSelects.Where(table => _linbits[table] == 0 && _widths[table] == width).ToArray();
            }

            return candidates;
        }

        private static int[] BuildCount1Bits()
        {
            var bits = new int[16];
            for (var pattern = 0; pattern < 16; pattern++)
            {
                bits[pattern] = Mp3HuffmanTables.TableA.GetCode(pattern).Length;
            }

            return bits;
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

        private static int[][] BuildPairSymbolBits()
        {
            var symbolBits = new int[32][];
            foreach (var select in _tableSelects)
            {
                var (table, width, linbits) = Mp3HuffmanTables.GetBigValueTable(select);
                _widths[select] = width;
                _linbits[select] = linbits;
                var bits = new int[SymbolCount];
                for (var x = 0; x < width; x++)
                {
                    for (var y = 0; y < width; y++)
                    {
                        var escapeBits = linbits > 0 ? ((x == width - 1 ? 1 : 0) + (y == width - 1 ? 1 : 0)) * linbits : 0;
                        bits[(x * (SymbolMagnitudeLimit + 1)) + y] = table.GetCode((x * width) + y).Length + (x != 0 ? 1 : 0) + (y != 0 ? 1 : 0) + escapeBits;
                    }
                }

                symbolBits[select] = bits;
            }

            return symbolBits;
        }

        private static void WritePair(BitWriter writer, HuffmanTable table, int maxDirect, int linbits, int x, int y)
        {
            var absX = Math.Abs(x);
            var absY = Math.Abs(y);
            var (code, length) = table.GetCode((Math.Min(absX, maxDirect) * (maxDirect + 1)) + Math.Min(absY, maxDirect));
            ulong bits = code;
            var count = length;
            AppendValueBits(ref bits, ref count, x, absX, maxDirect, linbits);
            AppendValueBits(ref bits, ref count, y, absY, maxDirect, linbits);
            writer.WriteBits(bits, count);
        }

        private static void AppendValueBits(ref ulong bits, ref int count, int value, int magnitude, int maxDirect, int linbits)
        {
            if (linbits > 0 && magnitude >= maxDirect)
            {
                bits = (bits << linbits) | (uint)(magnitude - maxDirect);
                count += linbits;
            }

            var nonZero = (int)((uint)(value | -value) >> 31);
            bits = (bits << nonZero) | (uint)((value >>> 31) & nonZero);
            count += nonZero;
        }
    }
}
