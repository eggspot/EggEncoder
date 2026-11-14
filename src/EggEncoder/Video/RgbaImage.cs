namespace EggEncoder.Video;

/// <summary>
/// An 8-bit RGBA picture with straight (not premultiplied) alpha, row after row with no gaps (4 × width bytes per
/// row). Used for overlays such as logos; video frames themselves are <see cref="VideoFrame"/>s.
/// </summary>
public sealed class RgbaImage
{
    private readonly byte[] _pixels;

    /// <summary>A transparent black image.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is negative.</exception>
    public RgbaImage(int width, int height)
        : this(width, height, new byte[CheckedLength(width, height)])
    {
    }

    /// <summary>Wraps <paramref name="pixels"/> without copying it.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="pixels"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is negative.</exception>
    /// <exception cref="ArgumentException"><paramref name="pixels"/> is not exactly width × height × 4 bytes.</exception>
    public RgbaImage(int width, int height, byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.LongLength != CheckedLength(width, height))
        {
            throw new ArgumentException($"A {width}x{height} RGBA image needs {(long)width * height * 4} bytes, not {pixels.LongLength}", nameof(pixels));
        }

        Width = width;
        Height = height;
        _pixels = pixels;
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>The pixels, R, G, B, A for each, row by row.</summary>
    public Span<byte> Pixels => _pixels;

    /// <summary>The pixel at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The position is outside the image.</exception>
    public RgbaColor GetPixel(int x, int y)
    {
        var index = IndexOf(x, y);
        return new RgbaColor(_pixels[index], _pixels[index + 1], _pixels[index + 2], _pixels[index + 3]);
    }

    /// <summary>Sets the pixel at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The position is outside the image.</exception>
    public void SetPixel(int x, int y, RgbaColor color)
    {
        var index = IndexOf(x, y);
        _pixels[index] = color.R;
        _pixels[index + 1] = color.G;
        _pixels[index + 2] = color.B;
        _pixels[index + 3] = color.A;
    }

    private int IndexOf(int x, int y)
    {
        if ((uint)x >= (uint)Width)
        {
            throw new ArgumentOutOfRangeException(nameof(x), x, $"x must be in 0..{Width - 1}");
        }

        if ((uint)y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(nameof(y), y, $"y must be in 0..{Height - 1}");
        }

        return ((y * Width) + x) * 4;
    }

    private static long CheckedLength(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        var length = (long)width * height * 4;
        if (length > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "The image is too large to hold in one array");
        }

        return length;
    }
}

/// <summary>
/// An 8-bit coverage mask (0 = nothing, 255 = fully covered), row after row with no gaps. Text, outlines and shadows
/// are rasterized into masks and drawn onto frames with one colour each.
/// </summary>
public sealed class AlphaMask
{
    private readonly byte[] _coverage;

    /// <summary>An empty mask.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is negative or the mask is too large.</exception>
    public AlphaMask(int width, int height)
        : this(width, height, new byte[CheckedLength(width, height)])
    {
    }

    /// <summary>Wraps <paramref name="coverage"/> without copying it.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="coverage"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is negative.</exception>
    /// <exception cref="ArgumentException"><paramref name="coverage"/> is not exactly width × height bytes.</exception>
    public AlphaMask(int width, int height, byte[] coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        if (coverage.LongLength != CheckedLength(width, height))
        {
            throw new ArgumentException($"A {width}x{height} mask needs {(long)width * height} bytes, not {coverage.LongLength}", nameof(coverage));
        }

        Width = width;
        Height = height;
        _coverage = coverage;
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>The coverage values, row by row.</summary>
    public Span<byte> Coverage => _coverage;

    private static long CheckedLength(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        var length = (long)width * height;
        if (length > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "The mask is too large to hold in one array");
        }

        return length;
    }
}
