using System.Collections.Concurrent;
using System.Numerics;

namespace EggEncoder.Video.Text;

internal sealed class PairKerning
{
    private const int MaxLookups = 4096;
    private const int MaxCachedPairs = 65536;

    private readonly Dictionary<(int, int), int>? _pairs;
    private readonly List<FontDataReader[]>? _lookups;
    private readonly ConcurrentDictionary<(int, int), int> _cache = new();

    private PairKerning(Dictionary<(int, int), int>? pairs, List<FontDataReader[]>? lookups)
    {
        _pairs = pairs;
        _lookups = lookups;
    }

    public static PairKerning None { get; } = new(null, null);

    public static PairKerning FromKernTable(FontDataReader kern)
    {
        var pairs = new Dictionary<(int, int), int>();
        if (kern.U16(0) != 0)
        {
            return None;
        }

        var tables = kern.U16(2);
        var position = 4;
        for (var t = 0; t < tables; t++)
        {
            var length = kern.U16(position + 2);
            var coverage = kern.U16(position + 4);
            var horizontal = (coverage & 0x1) != 0;
            var minimum = (coverage & 0x2) != 0;
            var crossStream = (coverage & 0x4) != 0;
            if ((coverage >> 8) == 0 && horizontal && !minimum && !crossStream)
            {
                var count = kern.U16(position + 6);
                kern.Require(position + 14, count * 6);
                for (var i = 0; i < count; i++)
                {
                    var record = position + 14 + (6 * i);
                    pairs[(kern.U16(record), kern.U16(record + 2))] = kern.I16(record + 4);
                }
            }

            if (length < 6)
            {
                break;
            }

            position += length;
        }

        return new PairKerning(pairs, null);
    }

    public static PairKerning FromGpos(FontDataReader gpos)
    {
        var featureList = gpos.Slice(gpos.U16(6), "GPOS");
        var lookupList = gpos.Slice(gpos.U16(8), "GPOS");
        var lookupIndices = new SortedSet<int>();
        var features = featureList.U16(0);
        for (var i = 0; i < features; i++)
        {
            var record = 2 + (6 * i);
            if (featureList.U32(record) != 0x6B65726E)
            {
                continue;
            }

            var feature = featureList.Slice(featureList.U16(record + 4));
            var count = feature.U16(2);
            for (var k = 0; k < count; k++)
            {
                lookupIndices.Add(feature.U16(4 + (2 * k)));
            }
        }

        var lookups = new List<FontDataReader[]>();
        var lookupCount = Math.Min((int)lookupList.U16(0), MaxLookups);
        foreach (var index in lookupIndices)
        {
            if (index >= lookupCount)
            {
                continue;
            }

            var lookup = lookupList.Slice(lookupList.U16(2 + (2 * index)));
            var type = lookup.U16(0);
            var subtableCount = lookup.U16(4);
            var subtables = new List<FontDataReader>();
            for (var s = 0; s < subtableCount; s++)
            {
                var subtable = lookup.Slice(lookup.U16(6 + (2 * s)));
                var subtableType = type;
                if (type == 9)
                {
                    subtableType = subtable.U16(2);
                    subtable = subtable.Slice(subtable.Offset32(4));
                }

                if (subtableType == 2)
                {
                    subtables.Add(subtable);
                }
            }

            lookups.Add([.. subtables]);
        }

        return new PairKerning(null, lookups);
    }

    public int Get(int left, int right)
    {
        if (_pairs is not null)
        {
            return _pairs.TryGetValue((left, right), out var value) ? value : 0;
        }

        if (_lookups is null || _lookups.Count == 0)
        {
            return 0;
        }

        if (_cache.TryGetValue((left, right), out var cached))
        {
            return cached;
        }

        if (_cache.Count >= MaxCachedPairs)
        {
            _cache.Clear();
        }

        var value = FromLookups(_lookups, left, right);
        _cache[(left, right)] = value;
        return value;
    }

    private static int FromLookups(List<FontDataReader[]> lookups, int left, int right)
    {
        var total = 0;
        foreach (var subtables in lookups)
        {
            foreach (var subtable in subtables)
            {
                if (PairAdjustment(subtable, left, right) is { } adjustment)
                {
                    total += adjustment;
                    break;
                }
            }
        }

        return total;
    }

    private static int? PairAdjustment(FontDataReader subtable, int left, int right)
    {
        var format = subtable.U16(0);
        var coverageIndex = CoverageIndex(subtable.Slice(subtable.U16(2)), left);
        if (coverageIndex < 0)
        {
            return null;
        }

        var valueFormat1 = subtable.U16(4);
        var valueFormat2 = subtable.U16(6);
        var size1 = 2 * BitOperations.PopCount(valueFormat1);
        var size2 = 2 * BitOperations.PopCount(valueFormat2);
        if (format == 1)
        {
            var pairSet = subtable.Slice(subtable.U16(10 + (2 * coverageIndex)));
            var count = pairSet.U16(0);
            var recordSize = 2 + size1 + size2;
            var low = 0;
            var high = count - 1;
            while (low <= high)
            {
                var middle = (low + high) >>> 1;
                var record = 2 + (middle * recordSize);
                var second = pairSet.U16(record);
                if (second == right)
                {
                    return XAdvance(pairSet, record + 2, valueFormat1);
                }

                if (second < right)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return null;
        }

        if (format == 2)
        {
            var class1 = ClassOf(subtable.Slice(subtable.U16(8)), left);
            var class2 = ClassOf(subtable.Slice(subtable.U16(10)), right);
            var class1Count = subtable.U16(12);
            var class2Count = subtable.U16(14);
            if (class1 >= class1Count || class2 >= class2Count)
            {
                return 0;
            }

            var record = 16 + (((class1 * class2Count) + class2) * (size1 + size2));
            return XAdvance(subtable, record, valueFormat1);
        }

        return null;
    }

    private static int XAdvance(FontDataReader table, int valueRecord, int valueFormat)
    {
        const int xAdvanceBit = 0x0004;
        if ((valueFormat & xAdvanceBit) == 0)
        {
            return 0;
        }

        return table.I16(valueRecord + (2 * BitOperations.PopCount((uint)(valueFormat & 0x0003))));
    }

    private static int CoverageIndex(FontDataReader coverage, int glyph)
    {
        var format = coverage.U16(0);
        var count = coverage.U16(2);
        var low = 0;
        var high = count - 1;
        while (low <= high)
        {
            var middle = (low + high) >>> 1;
            if (format == 1)
            {
                var value = coverage.U16(4 + (2 * middle));
                if (value == glyph)
                {
                    return middle;
                }

                if (value < glyph)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }
            else if (format == 2)
            {
                var record = 4 + (6 * middle);
                var start = coverage.U16(record);
                var end = coverage.U16(record + 2);
                if (glyph < start)
                {
                    high = middle - 1;
                }
                else if (glyph > end)
                {
                    low = middle + 1;
                }
                else
                {
                    return coverage.U16(record + 4) + glyph - start;
                }
            }
            else
            {
                return -1;
            }
        }

        return -1;
    }

    private static int ClassOf(FontDataReader classDefinition, int glyph)
    {
        var format = classDefinition.U16(0);
        if (format == 1)
        {
            var first = classDefinition.U16(2);
            var count = classDefinition.U16(4);
            return glyph >= first && glyph < first + count ? classDefinition.U16(6 + (2 * (glyph - first))) : 0;
        }

        if (format == 2)
        {
            var count = classDefinition.U16(2);
            var low = 0;
            var high = count - 1;
            while (low <= high)
            {
                var middle = (low + high) >>> 1;
                var record = 4 + (6 * middle);
                if (glyph < classDefinition.U16(record))
                {
                    high = middle - 1;
                }
                else if (glyph > classDefinition.U16(record + 2))
                {
                    low = middle + 1;
                }
                else
                {
                    return classDefinition.U16(record + 4);
                }
            }
        }

        return 0;
    }
}
