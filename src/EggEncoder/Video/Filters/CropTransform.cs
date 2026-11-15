namespace EggEncoder.Video.Filters;

/// <summary>
/// Keeps a rectangle of each frame. Its left and top edges are rounded down to even values so the chroma planes are
/// cut on whole samples, as ffmpeg's <c>crop</c> does on 4:2:0.
/// </summary>
public sealed class CropTransform : IVideoFrameTransform
{
    private readonly VideoRectangle _area;

    /// <param name="area">The rectangle to keep; it must lie inside every frame (checked per frame).</param>
    /// <exception cref="ArgumentOutOfRangeException">The rectangle has a negative position or a size below 1.</exception>
    public CropTransform(VideoRectangle area)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(area.X, nameof(area));
        ArgumentOutOfRangeException.ThrowIfNegative(area.Y, nameof(area));
        ArgumentOutOfRangeException.ThrowIfLessThan(area.Width, 1, nameof(area));
        ArgumentOutOfRangeException.ThrowIfLessThan(area.Height, 1, nameof(area));
        _area = area with { X = area.X & ~1, Y = area.Y & ~1 };
    }

    /// <inheritdoc />
    public (int Width, int Height) GetOutputSize(int inputWidth, int inputHeight) => (_area.Width, _area.Height);

    /// <inheritdoc />
    public bool CanChangeFrameCount => false;

    /// <inheritdoc />
    /// <exception cref="ArgumentException">The rectangle is not inside the frame.</exception>
    public void Apply(VideoFrame frame, Action<VideoFrame> emit) => FrameTransformSupport.ApplyOwning(frame, emit, Crop);

    private VideoFrame Crop(VideoFrame frame)
    {
        if (!_area.FitsIn(frame.Width, frame.Height))
        {
            throw new ArgumentException($"The crop {_area} is not inside the {frame.Width}x{frame.Height} frame");
        }

        var output = frame.Pool.Rent(_area.Width, _area.Height, frame.Range, frame.Matrix);
        FrameTransformSupport.CopyPlane(frame, output, VideoPlane.Y, _area.X, _area.Y, 0, 0, _area.Width, _area.Height);
        FrameTransformSupport.CopyPlane(frame, output, VideoPlane.U, _area.X / 2, _area.Y / 2, 0, 0, output.ChromaWidth, output.ChromaHeight);
        FrameTransformSupport.CopyPlane(frame, output, VideoPlane.V, _area.X / 2, _area.Y / 2, 0, 0, output.ChromaWidth, output.ChromaHeight);
        output.Timestamp = frame.Timestamp;
        output.Duration = frame.Duration;
        return output;
    }
}

/// <summary>
/// Places each frame on a larger canvas filled with a colour. The position is rounded down to even values so the
/// chroma planes line up, as ffmpeg's <c>pad</c> does on 4:2:0.
/// </summary>
public sealed class PadTransform : IVideoFrameTransform
{
    private readonly int _canvasWidth;
    private readonly int _canvasHeight;
    private readonly int _x;
    private readonly int _y;
    private readonly YuvColor _color;

    /// <param name="canvasWidth">Output width, 1..16384.</param>
    /// <param name="canvasHeight">Output height, 1..16384.</param>
    /// <param name="x">Left edge of the frame on the canvas, 0 or more.</param>
    /// <param name="y">Top edge of the frame on the canvas, 0 or more.</param>
    /// <param name="color">The canvas colour, in the frames' colour space.</param>
    /// <exception cref="ArgumentOutOfRangeException">A size or position is out of range.</exception>
    public PadTransform(int canvasWidth, int canvasHeight, int x, int y, YuvColor color)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(canvasWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(canvasHeight, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(canvasWidth, 16384);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(canvasHeight, 16384);
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        _canvasWidth = canvasWidth;
        _canvasHeight = canvasHeight;
        _x = x & ~1;
        _y = y & ~1;
        _color = color;
    }

    /// <inheritdoc />
    public (int Width, int Height) GetOutputSize(int inputWidth, int inputHeight) => (_canvasWidth, _canvasHeight);

    /// <inheritdoc />
    public bool CanChangeFrameCount => false;

    /// <inheritdoc />
    /// <exception cref="ArgumentException">The frame does not fit on the canvas at the position.</exception>
    public void Apply(VideoFrame frame, Action<VideoFrame> emit) => FrameTransformSupport.ApplyOwning(frame, emit, Pad);

    private VideoFrame Pad(VideoFrame frame)
    {
        if (_x + frame.Width > _canvasWidth || _y + frame.Height > _canvasHeight)
        {
            throw new ArgumentException($"A {frame.Width}x{frame.Height} frame at ({_x}, {_y}) does not fit on a {_canvasWidth}x{_canvasHeight} canvas");
        }

        var output = frame.Pool.Rent(_canvasWidth, _canvasHeight, frame.Range, frame.Matrix);
        output.Fill(_color);
        VideoBlend.Overlay(output, frame, _x, _y);
        output.Timestamp = frame.Timestamp;
        output.Duration = frame.Duration;
        return output;
    }
}

/// <summary>How <see cref="FitTransform"/> fits a frame to its canvas.</summary>
public enum FitMode
{
    /// <summary>Scale the whole frame to fit inside the canvas and fill the rest with a colour (letterbox).</summary>
    Contain,

    /// <summary>Scale the frame to cover the whole canvas, cutting off what sticks out on two sides.</summary>
    Cover
}

/// <summary>
/// Fits each frame to a canvas, centred, keeping its aspect ratio: <see cref="FitMode.Contain"/> scales it to fit and
/// pads; <see cref="FitMode.Cover"/> scales the centred part of the frame with the canvas's aspect straight to the
/// canvas. Scaled sizes are rounded to even values when the canvas side is even.
/// </summary>
public sealed class FitTransform : IVideoFrameTransform
{
    private readonly int _canvasWidth;
    private readonly int _canvasHeight;
    private readonly FitMode _mode;
    private readonly YuvColor _padColor;
    private readonly ScaleKernel _kernel;
    private readonly int _maxDegreeOfParallelism;
    private (int Width, int Height) _preparedFor;
    private ScaleTransform? _scale;
    private PadTransform? _pad;

    /// <param name="canvasWidth">Output width, 1..16384.</param>
    /// <param name="canvasHeight">Output height, 1..16384.</param>
    /// <param name="mode">Contain (letterbox) or cover (crop).</param>
    /// <param name="padColor">The colour around a contained frame, in the frames' colour space.</param>
    /// <param name="kernel">The resampling filter.</param>
    /// <param name="maxDegreeOfParallelism">Threads to use: 1 by default, 0 or less for one per processor.</param>
    /// <exception cref="ArgumentOutOfRangeException">A size, the mode or the kernel is out of range.</exception>
    public FitTransform(int canvasWidth, int canvasHeight, FitMode mode, YuvColor padColor = default, ScaleKernel kernel = ScaleKernel.Bicubic, int maxDegreeOfParallelism = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(canvasWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(canvasHeight, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(canvasWidth, 16384);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(canvasHeight, 16384);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown fit mode");
        }

        if (!Enum.IsDefined(kernel))
        {
            throw new ArgumentOutOfRangeException(nameof(kernel), kernel, "Unknown scale kernel");
        }

        _canvasWidth = canvasWidth;
        _canvasHeight = canvasHeight;
        _mode = mode;
        _padColor = padColor;
        _kernel = kernel;
        _maxDegreeOfParallelism = maxDegreeOfParallelism;
    }

    /// <inheritdoc />
    public (int Width, int Height) GetOutputSize(int inputWidth, int inputHeight) => (_canvasWidth, _canvasHeight);

    /// <inheritdoc />
    public bool CanChangeFrameCount => false;

    internal (int Width, int Height, VideoRectangle? Source) Plan(int width, int height)
    {
        if (_mode == FitMode.Contain)
        {
            var factor = Math.Min(_canvasWidth / (double)width, _canvasHeight / (double)height);
            return (Fit(width * factor, _canvasWidth), Fit(height * factor, _canvasHeight), null);
        }

        if ((long)width * _canvasHeight > (long)height * _canvasWidth)
        {
            var sourceWidth = Math.Clamp(Even(height * (double)_canvasWidth / _canvasHeight, width), 1, width);
            return (_canvasWidth, _canvasHeight, new VideoRectangle((width - sourceWidth) / 2, 0, sourceWidth, height));
        }

        var sourceHeight = Math.Clamp(Even(width * (double)_canvasHeight / _canvasWidth, height), 1, height);
        return (_canvasWidth, _canvasHeight, new VideoRectangle(0, (height - sourceHeight) / 2, width, sourceHeight));
    }

    /// <inheritdoc />
    public void Apply(VideoFrame frame, Action<VideoFrame> emit)
    {
        if (_scale is null || _preparedFor != (frame.Width, frame.Height))
        {
            var (width, height, source) = Plan(frame.Width, frame.Height);
            _scale = new ScaleTransform(width, height, _kernel, source, _maxDegreeOfParallelism);
            _pad = _mode == FitMode.Contain ? new PadTransform(_canvasWidth, _canvasHeight, (_canvasWidth - width) / 2, (_canvasHeight - height) / 2, _padColor) : null;
            _preparedFor = (frame.Width, frame.Height);
        }

        if (_pad is null)
        {
            _scale.Apply(frame, emit);
        }
        else
        {
            _scale.Apply(frame, scaled => _pad.Apply(scaled, emit));
        }
    }

    private static int Fit(double size, int canvas) => Math.Clamp(canvas % 2 == 0 ? (int)Math.Round(size / 2) * 2 : (int)Math.Round(size), 1, canvas);

    private static int Even(double size, int limit) => limit % 2 == 0 ? (int)Math.Round(size / 2) * 2 : (int)Math.Round(size);
}

/// <summary>Rotates each frame clockwise by 90, 180 or 270 degrees; 90 and 270 swap width and height.</summary>
public sealed class RotateTransform : IVideoFrameTransform
{
    private readonly int _degrees;

    /// <param name="degrees">90, 180 or 270, clockwise.</param>
    /// <exception cref="ArgumentOutOfRangeException">Any other angle.</exception>
    public RotateTransform(int degrees)
    {
        if (degrees is not (90 or 180 or 270))
        {
            throw new ArgumentOutOfRangeException(nameof(degrees), degrees, "Rotation must be 90, 180 or 270 degrees");
        }

        _degrees = degrees;
    }

    /// <inheritdoc />
    public (int Width, int Height) GetOutputSize(int inputWidth, int inputHeight) =>
        _degrees == 180 ? (inputWidth, inputHeight) : (inputHeight, inputWidth);

    /// <inheritdoc />
    public bool CanChangeFrameCount => false;

    /// <inheritdoc />
    public void Apply(VideoFrame frame, Action<VideoFrame> emit) => FrameTransformSupport.ApplyOwning(frame, emit, Rotate);

    private VideoFrame Rotate(VideoFrame frame)
    {
        var (width, height) = GetOutputSize(frame.Width, frame.Height);
        var output = frame.Pool.Rent(width, height, frame.Range, frame.Matrix);
        RotatePlane(frame, output, VideoPlane.Y, frame.Width, frame.Height);
        RotatePlane(frame, output, VideoPlane.U, frame.ChromaWidth, frame.ChromaHeight);
        RotatePlane(frame, output, VideoPlane.V, frame.ChromaWidth, frame.ChromaHeight);
        output.Timestamp = frame.Timestamp;
        output.Duration = frame.Duration;
        return output;
    }

    private void RotatePlane(VideoFrame source, VideoFrame destination, VideoPlane plane, int width, int height)
    {
        var input = source.Buffer;
        var inputOffset = plane switch { VideoPlane.Y => source.OffsetY, VideoPlane.U => source.OffsetU, _ => source.OffsetV };
        var stride = source.StrideOf(plane);
        var outputHeight = _degrees == 180 ? height : width;
        for (var y = 0; y < outputHeight; y++)
        {
            var output = destination.GetRow(plane, y);
            switch (_degrees)
            {
                case 90:
                    for (var x = 0; x < output.Length; x++)
                    {
                        output[x] = input[inputOffset + ((height - 1 - x) * stride) + y];
                    }

                    break;
                case 180:
                    var row = inputOffset + ((height - 1 - y) * stride) + width - 1;
                    for (var x = 0; x < output.Length; x++)
                    {
                        output[x] = input[row - x];
                    }

                    break;
                default:
                    var column = inputOffset + width - 1 - y;
                    for (var x = 0; x < output.Length; x++)
                    {
                        output[x] = input[column + (x * stride)];
                    }

                    break;
            }
        }
    }
}
