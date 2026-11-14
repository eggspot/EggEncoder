namespace EggEncoder.Video;

/// <summary>
/// Reuses the pixel buffers of disposed <see cref="VideoFrame"/>s. Each buffer has the exact size its frame needs and
/// is kept for frames of the same size; at most <see cref="MaxRetainedPerSize"/> free buffers are kept per size and
/// the rest are left to the garbage collector, so the memory a pool holds is bounded and readable through
/// <see cref="RetainedBytes"/>. Thread-safe: a frame may be disposed on another thread than the one that rented it.
/// </summary>
public sealed class VideoFramePool
{
    private const int MaxDimension = 16384;
    private const int StrideAlignment = 64;

    private readonly Dictionary<(int Width, int Height, int Padding), Stack<byte[]>> _free = new();
    private readonly object _lock = new();
    private long _retainedBytes;
    private int _retainedCount;

    /// <param name="maxRetainedPerSize">How many free buffers to keep for each frame size, 0 or more.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxRetainedPerSize"/> is negative.</exception>
    public VideoFramePool(int maxRetainedPerSize = 8)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetainedPerSize);
        MaxRetainedPerSize = maxRetainedPerSize;
    }

    /// <summary>The pool <see cref="VideoFrame.Create"/> rents from.</summary>
    public static VideoFramePool Shared { get; } = new();

    /// <summary>The most free buffers kept for one frame size.</summary>
    public int MaxRetainedPerSize { get; }

    /// <summary>Free buffers the pool holds now.</summary>
    public int RetainedBufferCount
    {
        get
        {
            lock (_lock)
            {
                return _retainedCount;
            }
        }
    }

    /// <summary>Bytes held by the free buffers.</summary>
    public long RetainedBytes
    {
        get
        {
            lock (_lock)
            {
                return _retainedBytes;
            }
        }
    }

    /// <summary>
    /// A frame of the given size, with a reused buffer when one is free. Its planes are not cleared: they may hold the
    /// pixels of an earlier frame until written.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is outside 1..16384, or the range or matrix is unknown.</exception>
    public VideoFrame Rent(int width, int height, VideoColorRange range = VideoColorRange.Limited, VideoColorMatrix matrix = VideoColorMatrix.Bt709) =>
        Rent(width, height, range, matrix, padding: 0);

    /// <summary>Drops every free buffer.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _free.Clear();
            _retainedBytes = 0;
            _retainedCount = 0;
        }
    }

    internal VideoFrame Rent(int width, int height, VideoColorRange range, VideoColorMatrix matrix, int padding)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, MaxDimension);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, MaxDimension);
        if (!Enum.IsDefined(range))
        {
            throw new ArgumentOutOfRangeException(nameof(range), range, "Unknown colour range");
        }

        if (!Enum.IsDefined(matrix))
        {
            throw new ArgumentOutOfRangeException(nameof(matrix), matrix, "Unknown colour matrix");
        }

        var layout = Layout(width, height, padding);
        var buffer = TakeFree((width, height, padding)) ?? new byte[layout.BufferLength];
        return new VideoFrame(this, buffer, width, height, padding, layout, range, matrix);
    }

    internal void Return(byte[] buffer, int width, int height, int padding)
    {
        lock (_lock)
        {
            if (!_free.TryGetValue((width, height, padding), out var stack))
            {
                stack = new Stack<byte[]>();
                _free.Add((width, height, padding), stack);
            }

            if (stack.Count >= MaxRetainedPerSize)
            {
                return;
            }

            stack.Push(buffer);
            _retainedBytes += buffer.Length;
            _retainedCount++;
        }
    }

    internal static FrameLayout Layout(int width, int height, int padding)
    {
        var chromaPadding = padding / 2;
        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;
        var strideY = Align(width + (2 * padding));
        var strideUV = Align(chromaWidth + (2 * chromaPadding));
        var lumaBytes = strideY * (height + (2 * padding));
        var chromaBytes = strideUV * (chromaHeight + (2 * chromaPadding));
        var offsetY = (padding * strideY) + padding;
        var offsetU = lumaBytes + (chromaPadding * strideUV) + chromaPadding;
        return new FrameLayout(strideY, strideUV, offsetY, offsetU, offsetU + chromaBytes, lumaBytes + (2 * chromaBytes));
    }

    private static int Align(int value) => (value + StrideAlignment - 1) / StrideAlignment * StrideAlignment;

    private byte[]? TakeFree((int, int, int) key)
    {
        lock (_lock)
        {
            if (!_free.TryGetValue(key, out var stack) || stack.Count == 0)
            {
                return null;
            }

            var buffer = stack.Pop();
            _retainedBytes -= buffer.Length;
            _retainedCount--;
            return buffer;
        }
    }

    internal readonly record struct FrameLayout(int StrideY, int StrideUV, int OffsetY, int OffsetU, int OffsetV, int BufferLength);
}
