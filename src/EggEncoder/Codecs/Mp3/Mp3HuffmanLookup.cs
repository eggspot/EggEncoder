using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using EggEncoder.Transform;

namespace EggEncoder.Codecs.Mp3;

internal ref struct Mp3BitStream
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    public Mp3BitStream(ReadOnlySpan<byte> paddedData, int bitPosition)
    {
        _data = paddedData;
        _position = bitPosition;
    }

    public int Position
    {
        readonly get => _position;
        set => _position = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly uint Peek(int count)
    {
        var word = BinaryPrimitives.ReadUInt64BigEndian(_data[(_position >> 3)..]);
        return (uint)((word << (_position & 7)) >> (64 - count));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Skip(int count) => _position += count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Read(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        var value = Peek(count);
        _position += count;
        return (int)value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadBit() => Read(1);
}

internal sealed class Mp3HuffmanLookup
{
    private const int PrimaryBits = 8;
    private const int LeafLengthMask = 0xFF;

    private static readonly Mp3HuffmanLookup?[] BigValueLookups = BuildBigValueLookups();

    private readonly int[] _entries;

    private Mp3HuffmanLookup(HuffmanTable table, int symbols, int pairWidth)
    {
        var codes = new List<(uint Code, int Length, int Symbol)>();
        for (var symbol = 0; symbol < symbols; symbol++)
        {
            var (code, length) = table.GetCode(symbol);
            if (length > 0)
            {
                codes.Add((code, length, symbol));
            }
        }

        var subtableBits = new Dictionary<uint, int>();
        foreach (var (code, length, _) in codes)
        {
            if (length > PrimaryBits)
            {
                var prefix = code >> (length - PrimaryBits);
                subtableBits[prefix] = Math.Max(subtableBits.GetValueOrDefault(prefix), length - PrimaryBits);
            }
        }

        var size = 1 << PrimaryBits;
        var offsets = new Dictionary<uint, int>();
        foreach (var (prefix, bits) in subtableBits.OrderBy(pair => pair.Key))
        {
            offsets[prefix] = size;
            size += 1 << bits;
        }

        _entries = new int[size];
        foreach (var (prefix, bits) in subtableBits)
        {
            _entries[prefix] = int.MinValue | (offsets[prefix] << 5) | bits;
        }

        foreach (var (code, length, symbol) in codes)
        {
            var value = pairWidth > 0 ? ((symbol / pairWidth) << 4) | (symbol % pairWidth) : symbol;
            var leaf = (value << 8) | length;
            if (length <= PrimaryBits)
            {
                var first = (int)(code << (PrimaryBits - length));
                for (var i = 0; i < 1 << (PrimaryBits - length); i++)
                {
                    _entries[first + i] = leaf;
                }
            }
            else
            {
                var prefix = code >> (length - PrimaryBits);
                var bits = subtableBits[prefix];
                var extra = length - PrimaryBits;
                var suffix = (int)(code & ((1u << extra) - 1));
                var first = offsets[prefix] + (suffix << (bits - extra));
                for (var i = 0; i < 1 << (bits - extra); i++)
                {
                    _entries[first + i] = leaf;
                }
            }
        }
    }

    public static Mp3HuffmanLookup CountA { get; } = new(Mp3HuffmanTables.TableA, 16, 0);

    public static Mp3HuffmanLookup CountB { get; } = new(Mp3HuffmanTables.TableB, 16, 0);

    public static Mp3HuffmanLookup? ForTable(int tableSelect) => BigValueLookups[tableSelect];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Decode(ref Mp3BitStream bits)
    {
        var entry = _entries[bits.Peek(PrimaryBits)];
        if (entry < 0)
        {
            var subBits = entry & 31;
            var offset = (entry >> 5) & 0xFFFFFF;
            entry = _entries[offset + (int)(bits.Peek(PrimaryBits + subBits) & ((1u << subBits) - 1))];
        }

        bits.Skip(entry & LeafLengthMask);
        return entry >> 8;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Lookup(uint window)
    {
        var entry = _entries[window >> (32 - PrimaryBits)];
        if (entry < 0)
        {
            var subBits = entry & 31;
            var offset = (entry >> 5) & 0xFFFFFF;
            entry = _entries[offset + (int)((window << PrimaryBits) >> (32 - subBits))];
        }

        return entry;
    }

    private static Mp3HuffmanLookup?[] BuildBigValueLookups()
    {
        var lookups = new Mp3HuffmanLookup?[32];
        var shared = new Dictionary<HuffmanTable, Mp3HuffmanLookup>();
        for (var select = 1; select < 32; select++)
        {
            if (select is 4 or 14)
            {
                continue;
            }

            var (table, width, _) = Mp3HuffmanTables.GetBigValueTable(select);
            if (!shared.TryGetValue(table, out var lookup))
            {
                lookup = new Mp3HuffmanLookup(table, width * width, width);
                shared[table] = lookup;
            }

            lookups[select] = lookup;
        }

        return lookups;
    }
}
