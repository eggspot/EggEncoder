using System.Numerics;

namespace EggEncoder.Video.Filters;

/// <summary>
/// Blurs each frame, or a rectangle of it, with a box filter: every sample becomes the average of the
/// (2 × radius + 1)² samples around it, edges repeated, run <c>passes</c> times (three passes come close to a
/// Gaussian), like ffmpeg's <c>boxblur</c>. Luma and chroma have their own radius; a chroma radius counts chroma
/// samples. A region is aligned outwards to even edges and blurred from its own samples only.
/// </summary>
public sealed class BoxBlurTransform : IVideoFrameTransform
{
    private readonly int _lumaRadius;
    private readonly int _chromaRadius;
    private readonly int _passes;
    private readonly VideoRectangle? _region;
    private readonly int _maxDegreeOfParallelism;
    private int[] _rowSums = [];

    /// <param name="lumaRadius">Luma radius in pixels, 0 or more (0 leaves luma alone).</param>
    /// <param name="chromaRadius">Chroma radius in chroma samples, 0 or more.</param>
    /// <param name="passes">How many times to apply the box, 1 to 10.</param>
    /// <param name="region">The part of the frame to blur; the whole frame when null. Clipped to each frame.</param>
    /// <param name="maxDegreeOfParallelism">Threads to use: 1 by default, 0 or less for one per processor.</param>
    /// <exception cref="ArgumentOutOfRangeException">A radius is negative or the pass count is out of range.</exception>
    public BoxBlurTransform(int lumaRadius, int chromaRadius, int passes = 1, VideoRectangle? region = null, int maxDegreeOfParallelism = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lumaRadius);
        ArgumentOutOfRangeException.ThrowIfNegative(chromaRadius);
        ArgumentOutOfRangeException.ThrowIfLessThan(passes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(passes, 10);
        _lumaRadius = lumaRadius;
        _chromaRadius = chromaRadius;
        _passes = passes;
        _region = region;
        _maxDegreeOfParallelism = maxDegreeOfParallelism;
    }

    /// <inheritdoc />
    public (int Width, int Height) GetOutputSize(int inputWidth, int inputHeight) => (inputWidth, inputHeight);

    /// <inheritdoc />
    public bool CanChangeFrameCount => false;

    /// <inheritdoc />
    public void Apply(VideoFrame frame, Action<VideoFrame> emit) => FrameTransformSupport.ApplyOwning(frame, emit, Blur);

    private VideoFrame Blur(VideoFrame frame)
    {
        var area = (_region ?? new VideoRectangle(0, 0, frame.Width, frame.Height)).ClipTo(frame.Width, frame.Height);
        if (area.Width == 0)
        {
            return frame;
        }

        var left = area.X & ~1;
        var top = area.Y & ~1;
        var right = Math.Min(frame.Width, area.Right + (area.Right & 1));
        var bottom = Math.Min(frame.Height, area.Bottom + (area.Bottom & 1));
        var chroma = new VideoRectangle(left / 2, top / 2, ((right + 1) / 2) - (left / 2), ((bottom + 1) / 2) - (top / 2));
        var buffer = frame.Buffer;
        for (var pass = 0; pass < _passes; pass++)
        {
            BlurPlane(buffer, frame.OffsetY, frame.StrideY, new VideoRectangle(left, top, right - left, bottom - top), _lumaRadius);
            BlurPlane(buffer, frame.OffsetU, frame.StrideUV, chroma, _chromaRadius);
            BlurPlane(buffer, frame.OffsetV, frame.StrideUV, chroma, _chromaRadius);
        }

        return frame;
    }

    private void BlurPlane(byte[] buffer, int offset, int stride, VideoRectangle area, int radius)
    {
        if (radius == 0)
        {
            return;
        }

        var width = area.Width;
        var height = area.Height;
        if (_rowSums.Length < width * height)
        {
            _rowSums = new int[width * height];
        }

        var horizontal = _rowSums;
        FrameTransformSupport.ForRows(height, _maxDegreeOfParallelism, (start, end) =>
        {
            for (var row = start; row < end; row++)
            {
                SumRow(buffer.AsSpan(offset + ((area.Y + row) * stride) + area.X, width), horizontal.AsSpan(row * width, width), radius);
            }
        });

        var scale = 1f / ((2 * radius) + 1) / ((2 * radius) + 1);
        FrameTransformSupport.ForRows(width, _maxDegreeOfParallelism, (start, end) =>
        {
            var columnSum = new int[end - start];
            for (var k = -radius; k <= radius; k++)
            {
                AddRow(horizontal, width, Math.Clamp(k, 0, height - 1), start, columnSum, 1);
            }

            for (var y = 0; y < height; y++)
            {
                WriteAverages(columnSum, scale, buffer.AsSpan(offset + ((area.Y + y) * stride) + area.X + start, end - start));

                if (y + 1 < height)
                {
                    AddRow(horizontal, width, Math.Min(y + 1 + radius, height - 1), start, columnSum, 1);
                    AddRow(horizontal, width, Math.Max(y - radius, 0), start, columnSum, -1);
                }
            }
        });
    }

    private static void WriteAverages(int[] sums, float scale, Span<byte> output)
    {
        var x = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var block = Vector<byte>.Count;
            var lanes = Vector<int>.Count;
            var scaleVector = new Vector<float>(scale);
            var half = new Vector<float>(0.5f);
            for (; x <= output.Length - block; x += block)
            {
                var first = Vector.Narrow(Average(x), Average(x + lanes));
                var second = Vector.Narrow(Average(x + (2 * lanes)), Average(x + (3 * lanes)));
                Vector.Narrow(first, second).CopyTo(output[x..]);
            }

            Vector<uint> Average(int index) =>
                Vector.AsVectorUInt32(Vector.ConvertToInt32((Vector.ConvertToSingle(new Vector<int>(sums, index)) * scaleVector) + half));
        }

        for (; x < output.Length; x++)
        {
            output[x] = (byte)((sums[x] * scale) + 0.5f);
        }
    }

    private static unsafe void SumRow(ReadOnlySpan<byte> row, Span<int> sums, int radius)
    {
        var length = row.Length;
        var last = length - 1;
        fixed (byte* input = row)
        fixed (int* output = sums)
        {
            var sum = 0;
            for (var i = -radius; i <= radius; i++)
            {
                sum += input[Math.Clamp(i, 0, last)];
            }

            var middleStart = Math.Min(radius, length);
            var middleEnd = Math.Max(middleStart, length - radius - 1);
            var x = 0;
            for (; x < middleStart; x++)
            {
                output[x] = sum;
                sum += input[Math.Min(x + radius + 1, last)] - input[0];
            }

            for (; x < middleEnd; x++)
            {
                output[x] = sum;
                sum += input[x + radius + 1] - input[x - radius];
            }

            for (; x < length; x++)
            {
                output[x] = sum;
                sum += input[last] - input[Math.Max(x - radius, 0)];
            }
        }
    }

    private static void AddRow(int[] rows, int width, int row, int start, int[] columnSum, int sign)
    {
        var source = rows.AsSpan((row * width) + start, columnSum.Length);
        var x = 0;
        if (Vector.IsHardwareAccelerated)
        {
            for (; x <= columnSum.Length - Vector<int>.Count; x += Vector<int>.Count)
            {
                var target = columnSum.AsSpan(x);
                var value = new Vector<int>(source[x..]);
                (sign > 0 ? new Vector<int>(target) + value : new Vector<int>(target) - value).CopyTo(target);
            }
        }

        for (; x < columnSum.Length; x++)
        {
            columnSum[x] += sign * source[x];
        }
    }
}
