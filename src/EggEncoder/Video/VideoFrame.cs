namespace EggEncoder.Video;

/// <summary>
/// One picture in planar Y'CbCr 4:2:0 with 8 bits per sample: a full-size luma plane and two chroma planes of
/// (width + 1) / 2 × (height + 1) / 2, so odd sizes are allowed. Rows are <see cref="StrideY"/> / <see cref="StrideUV"/>
/// bytes apart (a multiple of 64). The frame carries its colour range and matrix and a timestamp.
/// <para>
/// A frame has one owner at a time and is not thread-safe; it may be handed to another thread together with its
/// ownership. <see cref="Dispose"/> returns its buffer to the <see cref="VideoFramePool"/> it came from and may be
/// called more than once; plane access and the copy and fill methods throw <see cref="ObjectDisposedException"/>
/// afterwards. Spans taken before <see cref="Dispose"/> must not be used after it.
/// </para>
/// </summary>
public sealed class VideoFrame : IDisposable
{
    private readonly VideoFramePool _pool;
    private readonly int _padding;
    private readonly int _offsetY;
    private readonly int _offsetU;
    private readonly int _offsetV;
    private byte[]? _buffer;

    internal VideoFrame(VideoFramePool pool, byte[] buffer, int width, int height, int padding, VideoFramePool.FrameLayout layout, VideoColorRange range, VideoColorMatrix matrix)
    {
        _pool = pool;
        _buffer = buffer;
        _padding = padding;
        _offsetY = layout.OffsetY;
        _offsetU = layout.OffsetU;
        _offsetV = layout.OffsetV;
        Width = width;
        Height = height;
        StrideY = layout.StrideY;
        StrideUV = layout.StrideUV;
        Range = range;
        Matrix = matrix;
    }

    /// <summary>
    /// A frame from <see cref="VideoFramePool.Shared"/>. Its planes are not cleared: they may hold the pixels of an
    /// earlier frame until written.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is outside 1..16384, or the range or matrix is unknown.</exception>
    public static VideoFrame Create(int width, int height, VideoColorRange range = VideoColorRange.Limited, VideoColorMatrix matrix = VideoColorMatrix.Bt709) =>
        VideoFramePool.Shared.Rent(width, height, range, matrix);

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>Width of the chroma planes: (<see cref="Width"/> + 1) / 2.</summary>
    public int ChromaWidth => (Width + 1) / 2;

    /// <summary>Height of the chroma planes: (<see cref="Height"/> + 1) / 2.</summary>
    public int ChromaHeight => (Height + 1) / 2;

    /// <summary>Bytes from one luma row to the next.</summary>
    public int StrideY { get; }

    /// <summary>Bytes from one chroma row to the next.</summary>
    public int StrideUV { get; }

    /// <summary>The range of the stored values.</summary>
    public VideoColorRange Range { get; set; }

    /// <summary>The matrix the values were made with.</summary>
    public VideoColorMatrix Matrix { get; set; }

    /// <summary>When the frame is shown, from the start of its stream.</summary>
    public TimeSpan Timestamp { get; set; }

    /// <summary>How long the frame is shown; <see cref="TimeSpan.Zero"/> when unknown.</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>The luma plane from its top-left pixel: <see cref="StrideY"/> × (Height − 1) + Width bytes.</summary>
    public Span<byte> PlaneY => Buffer.AsSpan(_offsetY, (StrideY * (Height - 1)) + Width);

    /// <summary>The Cb plane from its top-left sample: <see cref="StrideUV"/> × (ChromaHeight − 1) + ChromaWidth bytes.</summary>
    public Span<byte> PlaneU => Buffer.AsSpan(_offsetU, (StrideUV * (ChromaHeight - 1)) + ChromaWidth);

    /// <summary>The Cr plane from its top-left sample: <see cref="StrideUV"/> × (ChromaHeight − 1) + ChromaWidth bytes.</summary>
    public Span<byte> PlaneV => Buffer.AsSpan(_offsetV, (StrideUV * (ChromaHeight - 1)) + ChromaWidth);

    internal byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(VideoFrame));

    internal int Padding => _padding;

    internal int OffsetY => _offsetY;

    internal int OffsetU => _offsetU;

    internal int OffsetV => _offsetV;

    internal VideoFramePool Pool => _pool;

    /// <summary>The plane's stride.</summary>
    public int StrideOf(VideoPlane plane) => plane == VideoPlane.Y ? StrideY : StrideUV;

    /// <summary>One row of a plane, exactly as wide as the plane.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The row is outside the plane.</exception>
    public Span<byte> GetRow(VideoPlane plane, int row)
    {
        var (width, height, offset, stride) = plane switch
        {
            VideoPlane.Y => (Width, Height, _offsetY, StrideY),
            VideoPlane.U => (ChromaWidth, ChromaHeight, _offsetU, StrideUV),
            VideoPlane.V => (ChromaWidth, ChromaHeight, _offsetV, StrideUV),
            _ => throw new ArgumentOutOfRangeException(nameof(plane), plane, "Unknown plane")
        };
        if ((uint)row >= (uint)height)
        {
            throw new ArgumentOutOfRangeException(nameof(row), row, $"Row must be in 0..{height - 1}");
        }

        return Buffer.AsSpan(offset + (row * stride), width);
    }

    /// <summary>Sets every pixel to <paramref name="color"/>. Bytes between rows are left alone.</summary>
    public void Fill(YuvColor color)
    {
        FillPlane(VideoPlane.Y, color.Y);
        FillPlane(VideoPlane.U, color.U);
        FillPlane(VideoPlane.V, color.V);
    }

    /// <summary>A copy of the pixels, range, matrix, timestamp and duration, rented from the same pool.</summary>
    public VideoFrame Clone()
    {
        var clone = _pool.Rent(Width, Height, Range, Matrix, _padding);
        CopyTo(clone);
        return clone;
    }

    /// <summary>Copies the pixels, range, matrix, timestamp and duration into a frame of the same size.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is null.</exception>
    /// <exception cref="ArgumentException">The sizes differ.</exception>
    public void CopyTo(VideoFrame destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Width != Width || destination.Height != Height)
        {
            throw new ArgumentException($"The destination is {destination.Width}x{destination.Height}, not {Width}x{Height}", nameof(destination));
        }

        foreach (var plane in (ReadOnlySpan<VideoPlane>)[VideoPlane.Y, VideoPlane.U, VideoPlane.V])
        {
            var rows = plane == VideoPlane.Y ? Height : ChromaHeight;
            for (var row = 0; row < rows; row++)
            {
                GetRow(plane, row).CopyTo(destination.GetRow(plane, row));
            }
        }

        destination.Range = Range;
        destination.Matrix = Matrix;
        destination.Timestamp = Timestamp;
        destination.Duration = Duration;
    }

    /// <summary>Returns the buffer to its pool. Calling it again does nothing.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _buffer, null) is { } buffer)
        {
            _pool.Return(buffer, Width, Height, _padding);
        }
    }

    private void FillPlane(VideoPlane plane, byte value)
    {
        var rows = plane == VideoPlane.Y ? Height : ChromaHeight;
        for (var row = 0; row < rows; row++)
        {
            GetRow(plane, row).Fill(value);
        }
    }
}
