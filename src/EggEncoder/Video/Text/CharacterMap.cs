namespace EggEncoder.Video.Text;

internal sealed class CharacterMap
{
    private const int MaxGroups = 1 << 20;

    private readonly int[] _starts;
    private readonly int[] _ends;
    private readonly Func<int, int, int> _glyphFor;

    private CharacterMap(int[] starts, int[] ends, Func<int, int, int> glyphFor)
    {
        _starts = starts;
        _ends = ends;
        _glyphFor = glyphFor;
    }

    public static CharacterMap Read(FontDataReader cmap, int glyphCount)
    {
        var count = cmap.U16(2);
        FontDataReader? format4 = null;
        FontDataReader? format12 = null;
        var format4Rank = int.MaxValue;
        var format12Rank = int.MaxValue;
        for (var i = 0; i < count; i++)
        {
            var record = 4 + (8 * i);
            var platform = cmap.U16(record);
            var encoding = cmap.U16(record + 2);
            var subtable = cmap.Slice(cmap.Offset32(record + 4), "cmap");
            var format = subtable.U16(0);
            var rank = (platform, encoding) switch
            {
                (3, 10) or (3, 1) => 0,
                (0, 4) or (0, 6) or (0, 3) => 1,
                (0, _) => 2,
                _ => int.MaxValue
            };
            if (format == 12 && rank < format12Rank)
            {
                (format12, format12Rank) = (subtable, rank);
            }
            else if (format == 4 && rank < format4Rank)
            {
                (format4, format4Rank) = (subtable, rank);
            }
        }

        if (format12 is { } full)
        {
            return ReadFormat12(full, glyphCount);
        }

        if (format4 is { } basic)
        {
            return ReadFormat4(basic, glyphCount);
        }

        throw new InvalidDataException("Font: no Unicode character map in format 4 or 12");
    }

    public int Lookup(int codePoint)
    {
        var low = 0;
        var high = _starts.Length - 1;
        while (low <= high)
        {
            var middle = (low + high) >>> 1;
            if (codePoint < _starts[middle])
            {
                high = middle - 1;
            }
            else if (codePoint > _ends[middle])
            {
                low = middle + 1;
            }
            else
            {
                return _glyphFor(middle, codePoint);
            }
        }

        return 0;
    }

    private static CharacterMap ReadFormat12(FontDataReader table, int glyphCount)
    {
        var groups = table.U32(12);
        if (groups > MaxGroups)
        {
            throw new InvalidDataException("Font: the character map has too many groups");
        }

        table.Require(16, (int)groups * 12);
        var starts = new int[groups];
        var ends = new int[groups];
        var firstGlyphs = new int[groups];
        for (var i = 0; i < groups; i++)
        {
            starts[i] = (int)Math.Min(table.U32(16 + (12 * i)), int.MaxValue);
            ends[i] = (int)Math.Min(table.U32(20 + (12 * i)), int.MaxValue);
            firstGlyphs[i] = (int)Math.Min(table.U32(24 + (12 * i)), int.MaxValue);
        }

        return new CharacterMap(starts, ends, (group, codePoint) =>
        {
            var glyph = (long)firstGlyphs[group] + codePoint - starts[group];
            return glyph < glyphCount ? (int)glyph : 0;
        });
    }

    private static CharacterMap ReadFormat4(FontDataReader table, int glyphCount)
    {
        var segments = table.U16(6) / 2;
        var endsAt = 14;
        var startsAt = endsAt + (2 * segments) + 2;
        var deltasAt = startsAt + (2 * segments);
        var rangeOffsetsAt = deltasAt + (2 * segments);
        table.Require(rangeOffsetsAt, 2 * segments);
        var starts = new int[segments];
        var ends = new int[segments];
        for (var i = 0; i < segments; i++)
        {
            starts[i] = table.U16(startsAt + (2 * i));
            ends[i] = table.U16(endsAt + (2 * i));
        }

        return new CharacterMap(starts, ends, (segment, codePoint) =>
        {
            var delta = table.U16(deltasAt + (2 * segment));
            var rangeOffset = table.U16(rangeOffsetsAt + (2 * segment));
            int glyph;
            if (rangeOffset == 0)
            {
                glyph = (codePoint + delta) & 0xFFFF;
            }
            else
            {
                var address = rangeOffsetsAt + (2 * segment) + rangeOffset + (2 * (codePoint - starts[segment]));
                if (address + 2 > table.Length)
                {
                    return 0;
                }

                glyph = table.U16(address);
                glyph = glyph == 0 ? 0 : (glyph + delta) & 0xFFFF;
            }

            return glyph < glyphCount ? glyph : 0;
        });
    }
}
