namespace EggEncoder.Video.Filters;

/// <summary>
/// Draws an RGBA image (a logo, a sticker) onto each frame at a position, blended by the image's alpha times an
/// opacity, during an optional time window. The image is converted to the frame's colour space once per matrix and
/// range; negative and odd positions are fine, and whatever falls outside the frame is clipped.
/// </summary>
public sealed class OverlayTransform : IVideoFrameTransform
{
    private readonly RgbaImage _image;
    private readonly int _x;
    private readonly int _y;
    private readonly double _opacity;
    private readonly TimeSpan? _start;
    private readonly TimeSpan? _end;
    private (VideoColorMatrix Matrix, VideoColorRange Range)? _convertedFor;
    private VideoDraw.ImageLayer _layer;

    /// <param name="image">The image to draw.</param>
    /// <param name="x">Left edge on the frame, may be negative.</param>
    /// <param name="y">Top edge on the frame, may be negative.</param>
    /// <param name="opacity">Multiplies the image's alpha, 0 to 1.</param>
    /// <param name="start">First timestamp the image is shown (inclusive); always when null.</param>
    /// <param name="end">Timestamp the image stops being shown (exclusive); never when null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The opacity is outside 0..1.</exception>
    /// <exception cref="ArgumentException">The window ends before it starts.</exception>
    public OverlayTransform(RgbaImage image, int x, int y, double opacity = 1, TimeSpan? start = null, TimeSpan? end = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (double.IsNaN(opacity) || opacity is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(opacity), opacity, "Opacity must be between 0 and 1");
        }

        FrameTransformSupport.ValidateWindow(start, end);
        _image = image;
        _x = x;
        _y = y;
        _opacity = opacity;
        _start = start;
        _end = end;
    }

    /// <inheritdoc />
    public (int Width, int Height) GetOutputSize(int inputWidth, int inputHeight) => (inputWidth, inputHeight);

    /// <inheritdoc />
    public bool CanChangeFrameCount => false;

    /// <inheritdoc />
    public void Apply(VideoFrame frame, Action<VideoFrame> emit) => FrameTransformSupport.ApplyOwning(frame, emit, Draw);

    private VideoFrame Draw(VideoFrame frame)
    {
        if (!FrameTransformSupport.IsActive(frame.Timestamp, _start, _end) || _image.Width == 0 || _image.Height == 0)
        {
            return frame;
        }

        if (_convertedFor != (frame.Matrix, frame.Range))
        {
            _layer = Convert(frame.Matrix, frame.Range);
            _convertedFor = (frame.Matrix, frame.Range);
        }

        VideoDraw.Composite(frame, _x, _y, _image.Width, _image.Height, _layer);
        return frame;
    }

    private VideoDraw.ImageLayer Convert(VideoColorMatrix matrix, VideoColorRange range)
    {
        var count = _image.Width * _image.Height;
        var pixels = _image.PixelArray;
        var y = new byte[count];
        var u = new byte[count];
        var v = new byte[count];
        var alpha = new byte[count];
        for (var i = 0; i < count; i++)
        {
            var color = YuvColor.FromRgb(pixels[4 * i], pixels[(4 * i) + 1], pixels[(4 * i) + 2], matrix, range);
            y[i] = color.Y;
            u[i] = color.U;
            v[i] = color.V;
            alpha[i] = (byte)Math.Round(pixels[(4 * i) + 3] * _opacity);
        }

        return new VideoDraw.ImageLayer(y, u, v, alpha);
    }
}

/// <summary>Draws a filled, alpha-blended rectangle on each frame during an optional time window (ffmpeg <c>drawbox</c> with <c>t=fill</c>).</summary>
public sealed class FillBoxTransform : IVideoFrameTransform
{
    private readonly VideoRectangle _box;
    private readonly RgbaColor _color;
    private readonly TimeSpan? _start;
    private readonly TimeSpan? _end;

    /// <param name="box">The rectangle; clipped to each frame.</param>
    /// <param name="color">Its colour; the alpha sets how opaque it is.</param>
    /// <param name="start">First timestamp the box is drawn (inclusive); always when null.</param>
    /// <param name="end">Timestamp the box stops being drawn (exclusive); never when null.</param>
    /// <exception cref="ArgumentException">The window ends before it starts.</exception>
    public FillBoxTransform(VideoRectangle box, RgbaColor color, TimeSpan? start = null, TimeSpan? end = null)
    {
        FrameTransformSupport.ValidateWindow(start, end);
        _box = box;
        _color = color;
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
        if (FrameTransformSupport.IsActive(f.Timestamp, _start, _end))
        {
            VideoDraw.FillRectangle(f, _box, _color);
        }

        return f;
    });
}

/// <summary>Whether <see cref="VideoFadeTransform"/> fades from a colour or to it.</summary>
public enum VideoFadeDirection
{
    /// <summary>From the colour into the picture.</summary>
    In,

    /// <summary>From the picture into the colour.</summary>
    Out
}

/// <summary>
/// Fades frames from or to a solid colour, linearly over a time span by each frame's timestamp, as ffmpeg's
/// <c>fade</c> filter does. Before a fade-in starts the frame is the colour; after a fade-out ends, too.
/// </summary>
public sealed class VideoFadeTransform : IVideoFrameTransform
{
    private readonly VideoFadeDirection _direction;
    private readonly TimeSpan _start;
    private readonly TimeSpan _duration;
    private readonly YuvColor _color;

    /// <param name="direction">In or out.</param>
    /// <param name="start">When the fade starts.</param>
    /// <param name="duration">How long it takes, more than zero.</param>
    /// <param name="color">The colour, in the frames' colour space (black is 16/128/128 in limited range).</param>
    /// <exception cref="ArgumentOutOfRangeException">The direction is unknown or the duration is not positive.</exception>
    public VideoFadeTransform(VideoFadeDirection direction, TimeSpan start, TimeSpan duration, YuvColor color)
    {
        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown fade direction");
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        _direction = direction;
        _start = start;
        _duration = duration;
        _color = color;
    }

    /// <inheritdoc />
    public (int Width, int Height) GetOutputSize(int inputWidth, int inputHeight) => (inputWidth, inputHeight);

    /// <inheritdoc />
    public bool CanChangeFrameCount => false;

    /// <summary>How much of the colour is mixed in at a timestamp, 0 to 1.</summary>
    public double ColorAmountAt(TimeSpan timestamp)
    {
        var progress = Math.Clamp((timestamp - _start) / _duration, 0, 1);
        return _direction == VideoFadeDirection.In ? 1 - progress : progress;
    }

    /// <inheritdoc />
    public void Apply(VideoFrame frame, Action<VideoFrame> emit) => FrameTransformSupport.ApplyOwning(frame, emit, f =>
    {
        var weight = (int)Math.Round(ColorAmountAt(f.Timestamp) * 256);
        if (weight > 0)
        {
            Mix(f, VideoPlane.Y, f.Height, _color.Y, weight);
            Mix(f, VideoPlane.U, f.ChromaHeight, _color.U, weight);
            Mix(f, VideoPlane.V, f.ChromaHeight, _color.V, weight);
        }

        return f;
    });

    private static void Mix(VideoFrame frame, VideoPlane plane, int rows, byte value, int weight)
    {
        var constant = value * weight;
        var keep = 256 - weight;
        for (var row = 0; row < rows; row++)
        {
            var span = frame.GetRow(plane, row);
            for (var x = 0; x < span.Length; x++)
            {
                span[x] = (byte)(((span[x] * keep) + constant + 128) >> 8);
            }
        }
    }
}

/// <summary>Operations on two frames.</summary>
public static class VideoBlend
{
    /// <summary>
    /// Writes the mix of <paramref name="from"/> and <paramref name="to"/> into <paramref name="destination"/>:
    /// progress 0 is <paramref name="from"/>, 1 is <paramref name="to"/>. <paramref name="destination"/> may be either
    /// input; its timestamp is left alone.
    /// </summary>
    /// <exception cref="ArgumentNullException">A frame is null.</exception>
    /// <exception cref="ArgumentException">The frames differ in size or colour space.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The progress is outside 0..1.</exception>
    public static void Crossfade(VideoFrame from, VideoFrame to, double progress, VideoFrame destination)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(destination);
        if (double.IsNaN(progress) || progress is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(progress), progress, "Progress must be between 0 and 1");
        }

        foreach (var other in (ReadOnlySpan<VideoFrame>)[to, destination])
        {
            if (other.Width != from.Width || other.Height != from.Height)
            {
                throw new ArgumentException("Crossfaded frames must have the same size");
            }

            FrameTransformSupport.RequireSameColors(from, other, nameof(to));
        }

        var weight = (int)Math.Round(progress * 256);
        Mix(from, to, destination, VideoPlane.Y, from.Height, weight);
        Mix(from, to, destination, VideoPlane.U, from.ChromaHeight, weight);
        Mix(from, to, destination, VideoPlane.V, from.ChromaHeight, weight);
    }

    /// <summary>
    /// Copies <paramref name="source"/> onto <paramref name="destination"/> with its top-left corner at
    /// (<paramref name="x"/>, <paramref name="y"/>), opaque, clipped to the destination (picture in picture). Chroma is
    /// placed at (x / 2, y / 2) rounded down, so odd positions shift chroma by half a pixel.
    /// </summary>
    /// <exception cref="ArgumentNullException">A frame is null.</exception>
    /// <exception cref="ArgumentException">The frames differ in colour space.</exception>
    public static void Overlay(VideoFrame destination, VideoFrame source, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);
        FrameTransformSupport.RequireSameColors(destination, source, nameof(source));
        var visible = new VideoRectangle(x, y, source.Width, source.Height).ClipTo(destination.Width, destination.Height);
        if (visible.Width == 0)
        {
            return;
        }

        FrameTransformSupport.CopyPlane(source, destination, VideoPlane.Y, visible.X - x, visible.Y - y, visible.X, visible.Y, visible.Width, visible.Height);
        var chroma = new VideoRectangle(FloorHalf(x), FloorHalf(y), source.ChromaWidth, source.ChromaHeight).ClipTo(destination.ChromaWidth, destination.ChromaHeight);
        if (chroma.Width == 0)
        {
            return;
        }

        var sourceX = chroma.X - FloorHalf(x);
        var sourceY = chroma.Y - FloorHalf(y);
        FrameTransformSupport.CopyPlane(source, destination, VideoPlane.U, sourceX, sourceY, chroma.X, chroma.Y, chroma.Width, chroma.Height);
        FrameTransformSupport.CopyPlane(source, destination, VideoPlane.V, sourceX, sourceY, chroma.X, chroma.Y, chroma.Width, chroma.Height);
    }

    private static int FloorHalf(int value) => value >> 1;

    private static void Mix(VideoFrame from, VideoFrame to, VideoFrame destination, VideoPlane plane, int rows, int weight)
    {
        var keep = 256 - weight;
        for (var row = 0; row < rows; row++)
        {
            var a = from.GetRow(plane, row);
            var b = to.GetRow(plane, row);
            var output = destination.GetRow(plane, row);
            for (var x = 0; x < output.Length; x++)
            {
                output[x] = (byte)(((a[x] * keep) + (b[x] * weight) + 128) >> 8);
            }
        }
    }
}
