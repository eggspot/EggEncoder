using EggEncoder.Video.Filters;

namespace EggEncoder.Video.Text;

/// <summary>
/// Draws a text on each frame during an optional time window, at an anchor position with margins (ffmpeg
/// <c>drawtext</c>). The text is rendered once, on the first frame, wrapped to <c>maxWidth</c> or, when that is 0, to
/// the frame width minus both horizontal margins; it is rendered again only if the frame width changes. Not
/// thread-safe.
/// </summary>
public sealed class TextOverlayTransform : IVideoFrameTransform
{
    private readonly TextRenderer _renderer;
    private readonly string _text;
    private readonly TextStyle _style;
    private readonly TextAnchor _anchor;
    private readonly int _marginX;
    private readonly int _marginY;
    private readonly int _maxWidth;
    private readonly TimeSpan? _start;
    private readonly TimeSpan? _end;
    private RenderedText? _rendered;
    private int _renderedForWidth;

    /// <param name="renderer">Renders the text.</param>
    /// <param name="text">The text; '\n' breaks lines.</param>
    /// <param name="style">How it looks.</param>
    /// <param name="anchor">Where it goes on the frame.</param>
    /// <param name="marginX">Distance from the left or right edge for left and right anchors, 0 or more.</param>
    /// <param name="marginY">Distance from the top or bottom edge for top and bottom anchors, 0 or more.</param>
    /// <param name="maxWidth">Wrap width in pixels; 0 wraps to the frame width minus both margins.</param>
    /// <param name="start">First timestamp the text is shown (inclusive); always when null.</param>
    /// <param name="end">Timestamp the text stops being shown (exclusive); never when null.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The style, anchor, a margin or the width is out of range.</exception>
    /// <exception cref="ArgumentException">The window ends before it starts.</exception>
    public TextOverlayTransform(
        TextRenderer renderer,
        string text,
        TextStyle style,
        TextAnchor anchor = TextAnchor.BottomCenter,
        int marginX = 0,
        int marginY = 0,
        int maxWidth = 0,
        TimeSpan? start = null,
        TimeSpan? end = null)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(style);
        style.Validate();
        if (!Enum.IsDefined(anchor))
        {
            throw new ArgumentOutOfRangeException(nameof(anchor), anchor, "Unknown anchor");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(marginX);
        ArgumentOutOfRangeException.ThrowIfNegative(marginY);
        ArgumentOutOfRangeException.ThrowIfNegative(maxWidth);
        FrameTransformSupport.ValidateWindow(start, end);
        _renderer = renderer;
        _text = text;
        _style = style;
        _anchor = anchor;
        _marginX = marginX;
        _marginY = marginY;
        _maxWidth = maxWidth;
        _start = start;
        _end = end;
    }

    /// <inheritdoc />
    public (int Width, int Height) GetOutputSize(int inputWidth, int inputHeight) => (inputWidth, inputHeight);

    /// <inheritdoc />
    public bool CanChangeFrameCount => false;

    /// <inheritdoc />
    public void Apply(VideoFrame frame, Action<VideoFrame> emit) => FrameTransformSupport.ApplyOwning(frame, emit, f =>
    {
        if (!FrameTransformSupport.IsActive(f.Timestamp, _start, _end))
        {
            return f;
        }

        if (_rendered is null || _renderedForWidth != f.Width)
        {
            var width = _maxWidth > 0 ? _maxWidth : Math.Max(1, f.Width - (2 * _marginX));
            _rendered = _renderer.Render(_text, _style, width);
            _renderedForWidth = f.Width;
        }

        var (x, y) = _rendered.Place(_anchor, f.Width, f.Height, _marginX, _marginY);
        _rendered.DrawOn(f, x, y);
        return f;
    });
}
