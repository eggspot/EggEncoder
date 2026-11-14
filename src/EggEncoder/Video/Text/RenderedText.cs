using EggEncoder.Video.Filters;

namespace EggEncoder.Video.Text;

/// <summary>
/// Text rasterized by <see cref="TextRenderer.Render"/>: coverage masks for the fill, outline and shadow on one
/// canvas of <see cref="Width"/> × <see cref="Height"/> pixels (which also holds the background box), ready to draw
/// onto frames with <see cref="DrawOn"/>. Immutable once made.
/// </summary>
public sealed class RenderedText
{
    private RenderedText(TextStyle style, int lineCount, int width, int height, AlphaMask fill, AlphaMask? outline, AlphaMask? shadow, VideoRectangle textBounds, VideoRectangle? background)
    {
        Style = style;
        LineCount = lineCount;
        Width = width;
        Height = height;
        Fill = fill;
        Outline = outline;
        Shadow = shadow;
        TextBounds = textBounds;
        Background = background;
    }

    /// <summary>The style it was rendered with.</summary>
    public TextStyle Style { get; }

    /// <summary>How many lines the text was laid out on; 0 for empty text.</summary>
    public int LineCount { get; }

    /// <summary>Width of the canvas: the text plus outline, shadow and background box.</summary>
    public int Width { get; }

    /// <summary>Height of the canvas.</summary>
    public int Height { get; }

    /// <summary>The text's own coverage.</summary>
    public AlphaMask Fill { get; }

    /// <summary>The outline's coverage (the text widened by the outline width), or null when there is no outline.</summary>
    public AlphaMask? Outline { get; }

    /// <summary>The shadow's coverage (outline or text, moved by the shadow offset), or null when there is no shadow.</summary>
    public AlphaMask? Shadow { get; }

    /// <summary>Where the lines of text (and every glyph's ink) lie on the canvas.</summary>
    public VideoRectangle TextBounds { get; }

    /// <summary>The background box on the canvas, or null when there is none.</summary>
    public VideoRectangle? Background { get; }

    /// <summary>
    /// Draws the text with its canvas's top-left corner at (<paramref name="x"/>, <paramref name="y"/>): background
    /// box, shadow, outline, then fill. Clipped at the frame's edges.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
    public void DrawOn(VideoFrame frame, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (LineCount == 0)
        {
            return;
        }

        if (Background is { } box)
        {
            VideoDraw.FillRectangle(frame, box with { X = box.X + x, Y = box.Y + y }, Style.BackgroundColor);
        }

        if (Shadow is not null)
        {
            VideoDraw.BlendMask(frame, Shadow, x, y, Style.ShadowColor);
        }

        if (Outline is not null)
        {
            VideoDraw.BlendMask(frame, Outline, x, y, Style.OutlineColor);
        }

        VideoDraw.BlendMask(frame, Fill, x, y, Style.Color);
    }

    /// <summary>The top-left corner that puts the canvas at <paramref name="anchor"/> on a frame, moved inwards by the margins.</summary>
    public (int X, int Y) Place(TextAnchor anchor, int frameWidth, int frameHeight, int marginX = 0, int marginY = 0)
    {
        var column = (int)anchor % 3;
        var row = (int)anchor / 3;
        var x = column switch
        {
            0 => marginX,
            1 => (frameWidth - Width) / 2,
            _ => frameWidth - Width - marginX
        };
        var y = row switch
        {
            0 => marginY,
            1 => (frameHeight - Height) / 2,
            _ => frameHeight - Height - marginY
        };
        return (x, y);
    }

    internal static RenderedText Empty(TextStyle style) => new(style, 0, 0, 0, new AlphaMask(0, 0), null, null, default, null);

    internal static RenderedText Compose(TextStyle style, int lineCount, VideoRectangle textBox, List<(AlphaMask Mask, int X, int Y)> glyphs)
    {
        var outlineRadius = style.HasOutline ? (int)Math.Ceiling(style.OutlineWidth) : 0;
        var padding = style.HasBackground ? style.BackgroundPadding : 0;
        var shadowX = style.HasShadow ? style.ShadowOffsetX : 0;
        var shadowY = style.HasShadow ? style.ShadowOffsetY : 0;
        var left = Math.Max(Math.Max(outlineRadius, padding), outlineRadius - shadowX);
        var right = Math.Max(Math.Max(outlineRadius, padding), outlineRadius + shadowX);
        var top = Math.Max(Math.Max(outlineRadius, padding), outlineRadius - shadowY);
        var bottom = Math.Max(Math.Max(outlineRadius, padding), outlineRadius + shadowY);
        var width = textBox.Width + left + right;
        var height = textBox.Height + top + bottom;
        var originX = left - textBox.X;
        var originY = top - textBox.Y;

        var fill = new AlphaMask(width, height);
        foreach (var (mask, x, y) in glyphs)
        {
            Stamp(mask, fill, originX + x, originY + y);
        }

        var outline = style.HasOutline ? Dilate(fill, style.OutlineWidth) : null;
        var shadow = style.HasShadow ? Shifted(outline ?? fill, shadowX, shadowY) : null;
        var textBounds = new VideoRectangle(left, top, textBox.Width, textBox.Height);
        VideoRectangle? background = style.HasBackground
            ? new VideoRectangle(left - padding, top - padding, textBox.Width + (2 * padding), textBox.Height + (2 * padding))
            : null;
        return new RenderedText(style, lineCount, width, height, fill, outline, shadow, textBounds, background);
    }

    private static void Stamp(AlphaMask glyph, AlphaMask canvas, int x, int y)
    {
        var source = glyph.Coverage;
        var target = canvas.Coverage;
        for (var row = 0; row < glyph.Height; row++)
        {
            var targetRow = y + row;
            if ((uint)targetRow >= (uint)canvas.Height)
            {
                continue;
            }

            for (var column = 0; column < glyph.Width; column++)
            {
                var targetColumn = x + column;
                if ((uint)targetColumn >= (uint)canvas.Width)
                {
                    continue;
                }

                var index = (targetRow * canvas.Width) + targetColumn;
                target[index] = (byte)Math.Min(255, target[index] + source[(row * glyph.Width) + column]);
            }
        }
    }

    private static AlphaMask Dilate(AlphaMask fill, double radius)
    {
        var reach = (int)Math.Ceiling(radius + 0.5);
        var offsets = new List<(int Dx, int Dy, double Weight)>();
        for (var dy = -reach; dy <= reach; dy++)
        {
            for (var dx = -reach; dx <= reach; dx++)
            {
                var weight = Math.Clamp(radius + 0.5 - Math.Sqrt((dx * dx) + (dy * dy)), 0, 1);
                if (weight > 0)
                {
                    offsets.Add((dx, dy, weight));
                }
            }
        }

        var width = fill.Width;
        var height = fill.Height;
        var source = fill.Coverage;
        var output = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = source[(y * width) + x];
                if (value == 255)
                {
                    output[(y * width) + x] = 255;
                    continue;
                }

                var best = (double)value;
                foreach (var (dx, dy, weight) in offsets)
                {
                    var sx = x + dx;
                    var sy = y + dy;
                    if ((uint)sx < (uint)width && (uint)sy < (uint)height)
                    {
                        best = Math.Max(best, source[(sy * width) + sx] * weight);
                    }
                }

                output[(y * width) + x] = (byte)Math.Round(best);
            }
        }

        return new AlphaMask(width, height, output);
    }

    private static AlphaMask Shifted(AlphaMask mask, int dx, int dy)
    {
        var output = new AlphaMask(mask.Width, mask.Height);
        Stamp(mask, output, dx, dy);
        return output;
    }
}
