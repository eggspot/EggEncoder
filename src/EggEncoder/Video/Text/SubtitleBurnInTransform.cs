using System.Text;
using System.Text.RegularExpressions;
using EggEncoder.Video.Filters;

namespace EggEncoder.Video.Text;

/// <summary>One subtitle: text shown from <paramref name="Start"/> (inclusive) to <paramref name="End"/> (exclusive).</summary>
/// <param name="Start">When it appears.</param>
/// <param name="End">When it disappears.</param>
/// <param name="Text">The text; <c>\N</c> or '\n' breaks a line, <c>\h</c> is a non-breaking space and <c>{...}</c> override tags are removed.</param>
public sealed record SubtitleCue(TimeSpan Start, TimeSpan End, string Text);

/// <summary>
/// The look of burned-in subtitles in ASS/libass units, as ffmpeg's <c>subtitles</c> filter uses them with
/// <c>force_style</c>. Sizes are in script pixels of a <see cref="PlayResX"/> × <see cref="PlayResY"/> canvas and
/// are scaled to the frame: vertical sizes (font size, outline, shadow, vertical margin) by frame height /
/// <see cref="PlayResY"/>, horizontal margins by frame width / <see cref="PlayResX"/>. The defaults are those ffmpeg
/// gives subtitles converted from SRT.
/// </summary>
public sealed record SubtitleStyle
{
    /// <summary>Font size: the height of the font's ascent plus descent, as libass sizes fonts. Default 16.</summary>
    public double FontSize { get; init; } = 16;

    /// <summary>Text colour (ASS PrimaryColour). Default opaque white.</summary>
    public RgbaColor PrimaryColor { get; init; } = new(255, 255, 255);

    /// <summary>Outline colour (ASS OutlineColour), and the box colour with <see cref="BorderStyle"/> 3. Default opaque black.</summary>
    public RgbaColor OutlineColor { get; init; } = new(0, 0, 0);

    /// <summary>Shadow colour (ASS BackColour). Default opaque black.</summary>
    public RgbaColor BackColor { get; init; } = new(0, 0, 0);

    /// <summary>Outline width, or box padding with <see cref="BorderStyle"/> 3, 0 to 16. Default 1.</summary>
    public double Outline { get; init; } = 1;

    /// <summary>Shadow offset down and to the right, 0 to 16. Default 0.</summary>
    public double Shadow { get; init; }

    /// <summary>Left margin, 0 or more. Default 10.</summary>
    public int MarginL { get; init; } = 10;

    /// <summary>Right margin, 0 or more. Default 10.</summary>
    public int MarginR { get; init; } = 10;

    /// <summary>Distance from the bottom (bottom alignments) or top (top alignments), 0 or more. Default 10.</summary>
    public int MarginV { get; init; } = 10;

    /// <summary>Position on the numeric keypad: 1-3 bottom, 4-6 middle, 7-9 top (left, centre, right). Default 2.</summary>
    public int Alignment { get; init; } = 2;

    /// <summary>1 = outline and shadow, 3 = an opaque box behind each subtitle. Default 1.</summary>
    public int BorderStyle { get; init; } = 1;

    /// <summary>Script width the horizontal sizes are measured in. Default 384.</summary>
    public int PlayResX { get; init; } = 384;

    /// <summary>Script height the vertical sizes are measured in. Default 288.</summary>
    public int PlayResY { get; init; } = 288;

    /// <summary>Checks every value against its range.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside its range; the exception names the property.</exception>
    public void Validate()
    {
        Require(FontSize is > 0 and <= 1000, nameof(FontSize), FontSize);
        Require(Outline is >= 0 and <= 16, nameof(Outline), Outline);
        Require(Shadow is >= 0 and <= 16, nameof(Shadow), Shadow);
        Require(MarginL >= 0, nameof(MarginL), MarginL);
        Require(MarginR >= 0, nameof(MarginR), MarginR);
        Require(MarginV >= 0, nameof(MarginV), MarginV);
        Require(Alignment is >= 1 and <= 9, nameof(Alignment), Alignment);
        Require(BorderStyle is 1 or 3, nameof(BorderStyle), BorderStyle);
        Require(PlayResX > 0, nameof(PlayResX), PlayResX);
        Require(PlayResY > 0, nameof(PlayResY), PlayResY);
    }

    private static void Require(bool valid, string name, object value)
    {
        if (!valid)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} is out of range");
        }
    }
}

/// <summary>
/// Burns subtitles into frames (ffmpeg <c>subtitles</c> with <c>force_style</c>), from cues given directly rather
/// than a subtitle file. Each cue is wrapped to the frame width minus the margins and rendered once per frame size;
/// cues shown at the same time stack away from their edge (upwards for bottom alignments), earliest start nearest
/// the edge. The font comes from the <see cref="TextRenderer"/>; glyphs look different from libass's (no hinting or
/// font fallback), which is intended. Not thread-safe.
/// </summary>
public sealed partial class SubtitleBurnInTransform : IVideoFrameTransform
{
    private readonly TextRenderer _renderer;
    private readonly SubtitleCue[] _cues;
    private readonly SubtitleStyle _style;
    private readonly Dictionary<int, RenderedText> _rendered = new();
    private (int Width, int Height) _renderedFor;

    /// <param name="renderer">Renders the text with its font.</param>
    /// <param name="cues">The subtitles, in any order.</param>
    /// <param name="style">Their look; ffmpeg's SRT defaults when null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="renderer"/>, <paramref name="cues"/> or a cue is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The style is out of range.</exception>
    /// <exception cref="ArgumentException">A cue ends before it starts.</exception>
    public SubtitleBurnInTransform(TextRenderer renderer, IReadOnlyList<SubtitleCue> cues, SubtitleStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(cues);
        _style = style ?? new SubtitleStyle();
        _style.Validate();
        foreach (var cue in cues)
        {
            ArgumentNullException.ThrowIfNull(cue, nameof(cues));
            ArgumentNullException.ThrowIfNull(cue.Text, nameof(cues));
            if (cue.End < cue.Start)
            {
                throw new ArgumentException($"A cue ends ({cue.End}) before it starts ({cue.Start})", nameof(cues));
            }
        }

        _renderer = renderer;
        _cues = [.. cues.OrderBy(c => c.Start)];
    }

    /// <inheritdoc />
    public (int Width, int Height) GetOutputSize(int inputWidth, int inputHeight) => (inputWidth, inputHeight);

    /// <inheritdoc />
    public bool CanChangeFrameCount => false;

    /// <summary>A cue's text as it is laid out: override tags removed, <c>\N</c> as a line break, <c>\h</c> as a non-breaking space, <c>\n</c> as a space.</summary>
    public static string PlainText(string assText)
    {
        ArgumentNullException.ThrowIfNull(assText);
        var text = OverrideTag().Replace(assText, string.Empty);
        return new StringBuilder(text).Replace("\\N", "\n").Replace("\\h", "\u00A0").Replace("\\n", " ").Replace("\r\n", "\n").ToString();
    }

    /// <inheritdoc />
    public void Apply(VideoFrame frame, Action<VideoFrame> emit) => FrameTransformSupport.ApplyOwning(frame, emit, f =>
    {
        var active = new List<int>();
        for (var i = 0; i < _cues.Length && _cues[i].Start <= f.Timestamp; i++)
        {
            if (f.Timestamp < _cues[i].End)
            {
                active.Add(i);
            }
        }

        if (active.Count == 0)
        {
            return f;
        }

        if (_renderedFor != (f.Width, f.Height))
        {
            _rendered.Clear();
            _renderedFor = (f.Width, f.Height);
        }

        var scaleX = f.Width / (double)_style.PlayResX;
        var scaleY = f.Height / (double)_style.PlayResY;
        var row = (_style.Alignment - 1) / 3;
        var column = (_style.Alignment - 1) % 3;
        var marginLeft = (int)Math.Round(_style.MarginL * scaleX);
        var marginRight = (int)Math.Round(_style.MarginR * scaleX);
        var marginVertical = (int)Math.Round(_style.MarginV * scaleY);
        var offset = 0;
        foreach (var index in active)
        {
            if (!_rendered.TryGetValue(index, out var text))
            {
                text = _renderer.Render(PlainText(_cues[index].Text), TextStyleFor(scaleY, column), Math.Max(1, f.Width - marginLeft - marginRight));
                _rendered[index] = text;
            }

            if (text.LineCount == 0)
            {
                continue;
            }

            var x = column switch
            {
                0 => marginLeft,
                1 => marginLeft + ((f.Width - marginLeft - marginRight - text.Width) / 2),
                _ => f.Width - marginRight - text.Width
            };
            var y = row switch
            {
                0 => f.Height - marginVertical - text.Height - offset,
                1 => ((f.Height - text.Height) / 2) + offset,
                _ => marginVertical + offset
            };
            text.DrawOn(f, x, y);
            offset += text.Height;
        }

        return f;
    });

    private TextStyle TextStyleFor(double scaleY, int column)
    {
        var font = _renderer.Font;
        var pixelsPerEm = _style.FontSize * scaleY * font.UnitsPerEm / Math.Max(1, font.WinAscent + font.WinDescent);
        var outline = _style.Outline * scaleY;
        var shadow = (int)Math.Round(_style.Shadow * scaleY);
        var alignment = column switch { 0 => TextAlignment.Left, 1 => TextAlignment.Center, _ => TextAlignment.Right };
        return _style.BorderStyle == 3
            ? new TextStyle
            {
                FontSize = Math.Min(2048, pixelsPerEm),
                Color = _style.PrimaryColor,
                BackgroundColor = _style.OutlineColor,
                BackgroundPadding = (int)Math.Round(outline),
                Alignment = alignment
            }
            : new TextStyle
            {
                FontSize = Math.Min(2048, pixelsPerEm),
                Color = _style.PrimaryColor,
                OutlineColor = _style.OutlineColor,
                OutlineWidth = Math.Min(64, outline),
                ShadowColor = _style.BackColor,
                ShadowOffsetX = Math.Min(256, shadow),
                ShadowOffsetY = Math.Min(256, shadow),
                Alignment = alignment
            };
    }

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex OverrideTag();
}
