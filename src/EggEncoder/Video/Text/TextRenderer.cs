using System.Globalization;
using System.Text;

namespace EggEncoder.Video.Text;

/// <summary>
/// Lays out and rasterizes text with one <see cref="TrueTypeFont"/>: Unicode NFC normalization (so "e" + combining
/// marks uses the font's precomposed glyph, as Vietnamese needs), line breaks at '\n', word wrapping to a width,
/// pair kerning, anti-aliased glyphs placed at quarter-pixel positions, and the outline, shadow and background box of
/// a <see cref="TextStyle"/>. Complex scripts that need shaping (Arabic, Indic) and mark positioning beyond
/// precomposed glyphs are not supported. Keeps a cache of up to 4096 rasterized glyphs, so an instance is not
/// thread-safe; use one per thread.
/// </summary>
public sealed class TextRenderer
{
    private const int MaxCachedGlyphs = 4096;
    private const int SubpixelSteps = 4;

    private readonly Dictionary<(int Glyph, double Size, int Subpixel), (AlphaMask Mask, int Left, int Top)> _glyphs = new();

    /// <exception cref="ArgumentNullException"><paramref name="font"/> is null.</exception>
    public TextRenderer(TrueTypeFont font)
    {
        ArgumentNullException.ThrowIfNull(font);
        Font = font;
    }

    /// <summary>The font text is drawn with.</summary>
    public TrueTypeFont Font { get; }

    /// <summary>The width in pixels of one line of text at a font size (sum of advances and kerning), without wrapping.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public double MeasureLine(string text, double fontSize)
    {
        ArgumentNullException.ThrowIfNull(text);
        return LineWidth(Glyphs(text.Normalize(NormalizationForm.FormC)), fontSize / Font.UnitsPerEm);
    }

    /// <summary>
    /// Renders <paramref name="text"/>. Lines break at '\n' and, when <paramref name="maxWidth"/> is above 0, between
    /// words so no line is wider than that many pixels (a word wider than a whole line is broken between characters).
    /// Empty or blank text gives a zero-size result.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The style is out of range or <paramref name="maxWidth"/> is negative.</exception>
    public RenderedText Render(string text, TextStyle style, int maxWidth = 0)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(style);
        style.Validate();
        ArgumentOutOfRangeException.ThrowIfNegative(maxWidth);

        var scale = style.FontSize / Font.UnitsPerEm;
        var lines = Wrap(text.Normalize(NormalizationForm.FormC).Replace("\r\n", "\n"), scale, maxWidth);
        if (lines.All(string.IsNullOrWhiteSpace))
        {
            return RenderedText.Empty(style);
        }

        var lineHeight = (Font.Ascender - Font.Descender + Font.LineGap) * scale * style.LineSpacing;
        var widths = lines.Select(line => LineWidth(Glyphs(line), scale)).ToList();
        var blockWidth = widths.Max();
        var placements = new List<(AlphaMask Mask, int X, int Y)>();
        for (var index = 0; index < lines.Count; index++)
        {
            var start = style.Alignment switch
            {
                TextAlignment.Center => (blockWidth - widths[index]) / 2,
                TextAlignment.Right => blockWidth - widths[index],
                _ => 0
            };
            var baseline = (int)Math.Round((Font.Ascender * scale) + (index * lineHeight));
            PlaceLine(Glyphs(lines[index]), scale, style.FontSize, start, baseline, placements);
        }

        var textBox = new VideoRectangle(0, 0, (int)Math.Ceiling(blockWidth), (int)Math.Ceiling(lines.Count * lineHeight));
        foreach (var (mask, x, y) in placements)
        {
            textBox = Union(textBox, new VideoRectangle(x, y, mask.Width, mask.Height));
        }

        return RenderedText.Compose(style, lines.Count, textBox, placements);
    }

    private List<string> Wrap(string text, double scale, int maxWidth)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            if (maxWidth == 0)
            {
                lines.Add(paragraph);
                continue;
            }

            var line = new StringBuilder();
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = line.Length == 0 ? word : $"{line} {word}";
                if (LineWidth(Glyphs(candidate), scale) <= maxWidth)
                {
                    line.Clear().Append(candidate);
                    continue;
                }

                if (line.Length > 0)
                {
                    lines.Add(line.ToString());
                    line.Clear();
                }

                foreach (var element in TextElements(word))
                {
                    if (line.Length > 0 && LineWidth(Glyphs(line + element), scale) > maxWidth)
                    {
                        lines.Add(line.ToString());
                        line.Clear();
                    }

                    line.Append(element);
                }
            }

            lines.Add(line.ToString());
        }

        return lines;
    }

    private static IEnumerable<string> TextElements(string text)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            yield return enumerator.GetTextElement();
        }
    }

    private List<int> Glyphs(string line)
    {
        var glyphs = new List<int>(line.Length);
        foreach (var rune in line.EnumerateRunes())
        {
            glyphs.Add(Font.GetGlyphIndex(rune.Value == 0xA0 ? ' ' : rune.Value));
        }

        return glyphs;
    }

    private double LineWidth(List<int> glyphs, double scale)
    {
        var width = 0.0;
        for (var i = 0; i < glyphs.Count; i++)
        {
            width += Font.GetAdvanceWidth(glyphs[i]) * scale;
            if (i + 1 < glyphs.Count)
            {
                width += Font.GetKerning(glyphs[i], glyphs[i + 1]) * scale;
            }
        }

        return width;
    }

    private void PlaceLine(List<int> glyphs, double scale, double fontSize, double penX, int baseline, List<(AlphaMask, int, int)> placements)
    {
        for (var i = 0; i < glyphs.Count; i++)
        {
            var whole = Math.Floor(penX);
            var subpixel = (int)Math.Round((penX - whole) * SubpixelSteps);
            if (subpixel == SubpixelSteps)
            {
                whole++;
                subpixel = 0;
            }

            var (mask, left, top) = Rasterized(glyphs[i], scale, fontSize, subpixel);
            if (mask.Width > 0)
            {
                placements.Add((mask, (int)whole + left, baseline + top));
            }

            penX += Font.GetAdvanceWidth(glyphs[i]) * scale;
            if (i + 1 < glyphs.Count)
            {
                penX += Font.GetKerning(glyphs[i], glyphs[i + 1]) * scale;
            }
        }
    }

    private (AlphaMask, int, int) Rasterized(int glyph, double scale, double fontSize, int subpixel)
    {
        var key = (glyph, fontSize, subpixel);
        if (_glyphs.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (_glyphs.Count >= MaxCachedGlyphs)
        {
            _glyphs.Clear();
        }

        var result = GlyphRasterizer.Rasterize(Font.GetOutline(glyph), scale, subpixel / (double)SubpixelSteps);
        _glyphs[key] = result;
        return result;
    }

    private static VideoRectangle Union(VideoRectangle a, VideoRectangle b)
    {
        var left = Math.Min(a.X, b.X);
        var top = Math.Min(a.Y, b.Y);
        return new VideoRectangle(left, top, Math.Max(a.Right, b.Right) - left, Math.Max(a.Bottom, b.Bottom) - top);
    }
}
