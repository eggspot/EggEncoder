namespace EggEncoder.Video.Text;

/// <summary>
/// A TrueType font (glyph outlines in a <c>glyf</c> table), parsed from the bytes of a .ttf file that the caller
/// supplies. Reads the metrics, the character map (formats 4 and 12), simple and composite glyphs, and pair
/// kerning from <c>GPOS</c> (the <c>kern</c> feature) or, when there is none, from a legacy <c>kern</c> table.
/// Fonts with CFF outlines (.otf) and font collections are not supported. Every offset in the file is checked, so a
/// damaged font throws <see cref="InvalidDataException"/> rather than reading out of bounds. Immutable and safe to
/// share between threads.
/// </summary>
public sealed class TrueTypeFont
{
    private const int MaxCompositeDepth = 8;

    private readonly byte[] _data;
    private readonly bool _longLocations;
    private readonly int _horizontalMetricCount;
    private readonly FontDataReader _hmtx;
    private readonly FontDataReader _loca;
    private readonly FontDataReader? _glyf;
    private readonly CharacterMap _characterMap;
    private readonly PairKerning _kerning;

    private TrueTypeFont(byte[] data)
    {
        _data = data;
        var tables = ReadTableDirectory(data);
        var head = Table(tables, "head");
        UnitsPerEm = head.U16(18);
        if (UnitsPerEm is < 16 or > 16384)
        {
            throw new InvalidDataException($"Font: unitsPerEm {UnitsPerEm} is outside 16..16384");
        }

        _longLocations = head.I16(50) != 0;
        var hhea = Table(tables, "hhea");
        Ascender = hhea.I16(4);
        Descender = hhea.I16(6);
        LineGap = hhea.I16(8);
        _horizontalMetricCount = hhea.U16(34);
        GlyphCount = Table(tables, "maxp").U16(4);
        if (_horizontalMetricCount == 0 || _horizontalMetricCount > GlyphCount)
        {
            throw new InvalidDataException("Font: the 'hhea' table's metric count is invalid");
        }

        _hmtx = Table(tables, "hmtx");
        _hmtx.Require(0, (_horizontalMetricCount * 4) + ((GlyphCount - _horizontalMetricCount) * 2));
        _loca = Table(tables, "loca");
        _loca.Require(0, (GlyphCount + 1) * (_longLocations ? 4 : 2));
        _glyf = tables.TryGetValue("glyf", out var glyf) ? glyf : null;
        if (_glyf is null)
        {
            throw new InvalidDataException("Font: there is no 'glyf' table");
        }

        if (tables.TryGetValue("OS/2", out var os2) && os2.Length >= 78)
        {
            WinAscent = os2.U16(74);
            WinDescent = os2.U16(76);
        }
        else
        {
            WinAscent = Ascender;
            WinDescent = -Descender;
        }

        _characterMap = CharacterMap.Read(Table(tables, "cmap"), GlyphCount);
        _kerning = tables.TryGetValue("GPOS", out var gpos)
            ? PairKerning.FromGpos(gpos)
            : tables.TryGetValue("kern", out var kern) ? PairKerning.FromKernTable(kern) : PairKerning.None;
    }

    /// <summary>Font units per em: glyph coordinates are in these units.</summary>
    public int UnitsPerEm { get; }

    /// <summary>Distance from the baseline to the top of a line, in font units (<c>hhea</c>).</summary>
    public int Ascender { get; }

    /// <summary>Distance from the baseline to the bottom of a line, in font units, usually negative (<c>hhea</c>).</summary>
    public int Descender { get; }

    /// <summary>Extra space between lines, in font units (<c>hhea</c>).</summary>
    public int LineGap { get; }

    /// <summary>The height above the baseline that every glyph fits under, in font units (<c>OS/2</c> usWinAscent, else the ascender).</summary>
    public int WinAscent { get; }

    /// <summary>The depth below the baseline that every glyph fits above, in font units, positive (<c>OS/2</c> usWinDescent, else −descender).</summary>
    public int WinDescent { get; }

    /// <summary>How many glyphs the font has.</summary>
    public int GlyphCount { get; }

    /// <summary>Parses a font from the bytes of a .ttf file. The bytes are copied.</summary>
    /// <exception cref="InvalidDataException">The data is not a well-formed TrueType font.</exception>
    /// <exception cref="NotSupportedException">The font has CFF outlines or is a collection.</exception>
    public static TrueTypeFont Load(ReadOnlySpan<byte> fontData) => new(fontData.ToArray());

    /// <summary>The glyph for a Unicode code point, or 0 (<c>.notdef</c>) when the font has none.</summary>
    public int GetGlyphIndex(int codePoint) => _characterMap.Lookup(codePoint);

    /// <summary>How far the pen moves after a glyph, in font units.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The glyph index is outside the font.</exception>
    public int GetAdvanceWidth(int glyph)
    {
        RequireGlyph(glyph);
        return _hmtx.U16(Math.Min(glyph, _horizontalMetricCount - 1) * 4);
    }

    /// <summary>The adjustment to the advance between two glyphs, in font units (negative brings them closer).</summary>
    public int GetKerning(int left, int right) => _kerning.Get(left, right);

    internal GlyphOutline GetOutline(int glyph)
    {
        RequireGlyph(glyph);
        var outline = new GlyphOutline();
        AppendGlyph(glyph, outline, new Transform(1, 0, 0, 1, 0, 0), 0);
        return outline;
    }

    private void AppendGlyph(int glyph, GlyphOutline outline, Transform transform, int depth)
    {
        if (depth > MaxCompositeDepth)
        {
            throw new InvalidDataException("Font: composite glyphs are nested too deeply");
        }

        var start = _longLocations ? _loca.Offset32(glyph * 4) : _loca.U16(glyph * 2) * 2;
        var end = _longLocations ? _loca.Offset32((glyph + 1) * 4) : _loca.U16((glyph + 1) * 2) * 2;
        if (end <= start)
        {
            return;
        }

        var data = new FontDataReader(_data, _glyf!.Value.Start + start, Math.Min(end, _glyf.Value.Length) - start, "glyf");
        var contours = data.I16(0);
        if (contours >= 0)
        {
            AppendSimpleGlyph(data, contours, outline, transform);
        }
        else
        {
            AppendCompositeGlyph(data, outline, transform, depth);
        }
    }

    private static void AppendSimpleGlyph(FontDataReader data, int contours, GlyphOutline outline, Transform transform)
    {
        var endPoints = new int[contours];
        var previous = -1;
        for (var i = 0; i < contours; i++)
        {
            endPoints[i] = data.U16(10 + (2 * i));
            if (endPoints[i] <= previous)
            {
                throw new InvalidDataException("Font: a glyph's contour end points are not increasing");
            }

            previous = endPoints[i];
        }

        var pointCount = contours == 0 ? 0 : endPoints[^1] + 1;
        var position = 10 + (2 * contours);
        position += 2 + data.U16(position);
        var flags = new byte[pointCount];
        for (var i = 0; i < pointCount;)
        {
            var flag = data.U8(position++);
            flags[i++] = flag;
            if ((flag & 0x08) != 0)
            {
                var repeat = data.U8(position++);
                for (var r = 0; r < repeat; r++)
                {
                    if (i >= pointCount)
                    {
                        throw new InvalidDataException("Font: a glyph's flags repeat past its last point");
                    }

                    flags[i++] = flag;
                }
            }
        }

        var xs = new int[pointCount];
        var ys = new int[pointCount];
        position = ReadCoordinates(data, position, flags, xs, 0x02, 0x10);
        ReadCoordinates(data, position, flags, ys, 0x04, 0x20);
        var first = outline.PointCount;
        for (var i = 0; i < pointCount; i++)
        {
            var (x, y) = transform.Apply(xs[i], ys[i]);
            outline.AddPoint(x, y, (flags[i] & 0x01) != 0);
        }

        foreach (var endPoint in endPoints)
        {
            outline.EndContour(first + endPoint);
        }
    }

    private static int ReadCoordinates(FontDataReader data, int position, byte[] flags, int[] values, int shortBit, int sameOrPositiveBit)
    {
        var value = 0;
        for (var i = 0; i < flags.Length; i++)
        {
            var flag = flags[i];
            if ((flag & shortBit) != 0)
            {
                var delta = data.U8(position++);
                value += (flag & sameOrPositiveBit) != 0 ? delta : -delta;
            }
            else if ((flag & sameOrPositiveBit) == 0)
            {
                value += data.I16(position);
                position += 2;
            }

            values[i] = value;
        }

        return position;
    }

    private void AppendCompositeGlyph(FontDataReader data, GlyphOutline outline, Transform transform, int depth)
    {
        const int argsAreWords = 0x0001;
        const int argsAreXyValues = 0x0002;
        const int haveScale = 0x0008;
        const int moreComponents = 0x0020;
        const int haveXyScale = 0x0040;
        const int haveTwoByTwo = 0x0080;
        const int scaledComponentOffset = 0x0800;
        const int unscaledComponentOffset = 0x1000;

        var position = 10;
        int flags;
        do
        {
            flags = data.U16(position);
            var component = data.U16(position + 2);
            position += 4;
            int argument1;
            int argument2;
            if ((flags & argsAreWords) != 0)
            {
                argument1 = (flags & argsAreXyValues) != 0 ? data.I16(position) : data.U16(position);
                argument2 = (flags & argsAreXyValues) != 0 ? data.I16(position + 2) : data.U16(position + 2);
                position += 4;
            }
            else
            {
                argument1 = (flags & argsAreXyValues) != 0 ? data.I8(position) : data.U8(position);
                argument2 = (flags & argsAreXyValues) != 0 ? data.I8(position + 1) : data.U8(position + 1);
                position += 2;
            }

            double a = 1, b = 0, c = 0, d = 1;
            if ((flags & haveScale) != 0)
            {
                a = d = data.F2Dot14(position);
                position += 2;
            }
            else if ((flags & haveXyScale) != 0)
            {
                a = data.F2Dot14(position);
                d = data.F2Dot14(position + 2);
                position += 4;
            }
            else if ((flags & haveTwoByTwo) != 0)
            {
                a = data.F2Dot14(position);
                b = data.F2Dot14(position + 2);
                c = data.F2Dot14(position + 4);
                d = data.F2Dot14(position + 6);
                position += 8;
            }

            RequireGlyph(component, "a composite glyph names a glyph outside the font");
            var first = outline.PointCount;
            if ((flags & argsAreXyValues) != 0)
            {
                double dx = argument1;
                double dy = argument2;
                if ((flags & scaledComponentOffset) != 0 && (flags & unscaledComponentOffset) == 0)
                {
                    (dx, dy) = ((a * dx) + (c * dy), (b * dx) + (d * dy));
                }

                AppendGlyph(component, outline, transform.Then(new Transform(a, b, c, d, dx, dy)), depth + 1);
            }
            else
            {
                AppendGlyph(component, outline, transform.Then(new Transform(a, b, c, d, 0, 0)), depth + 1);
                var parentPoint = argument1;
                var childPoint = first + argument2;
                if (parentPoint >= first || childPoint >= outline.PointCount)
                {
                    throw new InvalidDataException("Font: a composite glyph matches a point it doesn't have");
                }

                outline.Shift(first, outline.X(parentPoint) - outline.X(childPoint), outline.Y(parentPoint) - outline.Y(childPoint));
            }
        }
        while ((flags & moreComponents) != 0);
    }

    private void RequireGlyph(int glyph, string? message = null)
    {
        if ((uint)glyph >= (uint)GlyphCount)
        {
            if (message is not null)
            {
                throw new InvalidDataException($"Font: {message}");
            }

            throw new ArgumentOutOfRangeException(nameof(glyph), glyph, $"Glyph must be in 0..{GlyphCount - 1}");
        }
    }

    private static Dictionary<string, FontDataReader> ReadTableDirectory(byte[] data)
    {
        var file = new FontDataReader(data, 0, data.Length, "table directory");
        var version = file.U32(0);
        if (version == 0x4F54544F)
        {
            throw new NotSupportedException("Font: CFF outlines (OpenType 'OTTO') are not supported; use a TrueType (glyf) font");
        }

        if (version == 0x74746366)
        {
            throw new NotSupportedException("Font: font collections (.ttc) are not supported; extract one font");
        }

        if (version != 0x00010000 && version != 0x74727565)
        {
            throw new InvalidDataException("Font: not a TrueType font");
        }

        var count = file.U16(4);
        var tables = new Dictionary<string, FontDataReader>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var record = 12 + (16 * i);
            var tag = string.Create(4, (file, record), static (chars, state) =>
            {
                for (var k = 0; k < 4; k++)
                {
                    chars[k] = (char)state.file.U8(state.record + k);
                }
            });
            tables[tag] = new FontDataReader(data, file.Offset32(record + 8), file.Offset32(record + 12), tag);
        }

        return tables;
    }

    private static FontDataReader Table(Dictionary<string, FontDataReader> tables, string tag) =>
        tables.TryGetValue(tag, out var table) ? table : throw new InvalidDataException($"Font: there is no '{tag}' table");

    private readonly record struct Transform(double A, double B, double C, double D, double Dx, double Dy)
    {
        public (double X, double Y) Apply(double x, double y) => ((A * x) + (C * y) + Dx, (B * x) + (D * y) + Dy);

        public Transform Then(Transform inner) =>
            new(
                (A * inner.A) + (C * inner.B),
                (B * inner.A) + (D * inner.B),
                (A * inner.C) + (C * inner.D),
                (B * inner.C) + (D * inner.D),
                (A * inner.Dx) + (C * inner.Dy) + Dx,
                (B * inner.Dx) + (D * inner.Dy) + Dy);
    }
}
