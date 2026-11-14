namespace EggEncoder.Video.Filters;

/// <summary>The resampling filter <see cref="ScaleTransform"/> uses.</summary>
public enum ScaleKernel
{
    /// <summary>Linear interpolation (2 taps when enlarging).</summary>
    Bilinear,

    /// <summary>Keys cubic with a = −0.6, ffmpeg's default bicubic (4 taps when enlarging).</summary>
    Bicubic,

    /// <summary>Lanczos with 3 lobes (6 taps when enlarging).</summary>
    Lanczos
}

/// <summary>
/// Resizes frames, optionally from a source rectangle (a crop done inside the scaler, so pixels that would be cut
/// away are never scaled). Separable: rows then columns, with weights worked out once per input size in 14-bit
/// fixed point; when shrinking the kernel is widened by the factor, so the result is low-pass filtered. Chroma is
/// sited as H.264 and ffmpeg site 4:2:0 by default: horizontally on the even luma columns, vertically between rows.
/// </summary>
public sealed class ScaleTransform : IVideoFrameTransform
{
    private readonly int _width;
    private readonly int _height;
    private readonly ScaleKernel _kernel;
    private readonly VideoRectangle? _source;
    private readonly int _maxDegreeOfParallelism;
    private (int Width, int Height) _preparedFor;
    private PlaneScaler? _luma;
    private PlaneScaler? _chroma;

    /// <param name="width">Output width, 1..16384.</param>
    /// <param name="height">Output height, 1..16384.</param>
    /// <param name="kernel">The resampling filter.</param>
    /// <param name="source">The part of each input frame to scale; the whole frame when null. Checked against each frame.</param>
    /// <param name="maxDegreeOfParallelism">Threads to use: 1 by default, 0 or less for one per processor.</param>
    /// <exception cref="ArgumentOutOfRangeException">A size is out of range or the kernel is unknown.</exception>
    public ScaleTransform(int width, int height, ScaleKernel kernel = ScaleKernel.Bicubic, VideoRectangle? source = null, int maxDegreeOfParallelism = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, 16384);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, 16384);
        if (!Enum.IsDefined(kernel))
        {
            throw new ArgumentOutOfRangeException(nameof(kernel), kernel, "Unknown scale kernel");
        }

        _width = width;
        _height = height;
        _kernel = kernel;
        _source = source;
        _maxDegreeOfParallelism = maxDegreeOfParallelism;
    }

    /// <inheritdoc />
    public (int Width, int Height) GetOutputSize(int inputWidth, int inputHeight) => (_width, _height);

    /// <inheritdoc />
    public bool CanChangeFrameCount => false;

    /// <inheritdoc />
    /// <exception cref="ArgumentException">The source rectangle is not inside the frame.</exception>
    public void Apply(VideoFrame frame, Action<VideoFrame> emit) => FrameTransformSupport.ApplyOwning(frame, emit, Scale);

    internal VideoFrame Scale(VideoFrame frame)
    {
        var source = _source ?? new VideoRectangle(0, 0, frame.Width, frame.Height);
        if (!source.FitsIn(frame.Width, frame.Height))
        {
            throw new ArgumentException($"The source rectangle {source} is not inside the {frame.Width}x{frame.Height} frame");
        }

        if (_luma is null || _preparedFor != (frame.Width, frame.Height))
        {
            (_luma, _chroma) = PlaneScaler.Create(_kernel, frame.Width, frame.Height, source, _width, _height);
            _preparedFor = (frame.Width, frame.Height);
        }

        var output = frame.Pool.Rent(_width, _height, frame.Range, frame.Matrix);
        try
        {
            var buffer = frame.Buffer;
            var target = output.Buffer;
            _luma.Scale(buffer, frame.OffsetY, frame.StrideY, target, output.OffsetY, output.StrideY, _maxDegreeOfParallelism);
            _chroma!.Scale(buffer, frame.OffsetU, frame.StrideUV, target, output.OffsetU, output.StrideUV, _maxDegreeOfParallelism);
            _chroma.Scale(buffer, frame.OffsetV, frame.StrideUV, target, output.OffsetV, output.StrideUV, _maxDegreeOfParallelism);
        }
        catch
        {
            output.Dispose();
            throw;
        }

        output.Timestamp = frame.Timestamp;
        output.Duration = frame.Duration;
        return output;
    }
}
