namespace EggEncoder.Video.Filters;

internal static class FrameTransformSupport
{
    public static bool IsActive(TimeSpan timestamp, TimeSpan? start, TimeSpan? end) =>
        (start is not { } from || timestamp >= from) && (end is not { } to || timestamp < to);

    public static void ValidateWindow(TimeSpan? start, TimeSpan? end)
    {
        if (start is { } from && end is { } to && to < from)
        {
            throw new ArgumentException("The end of the time window is before its start", nameof(end));
        }
    }

    public static int DegreeOfParallelism(int requested) => requested <= 0 ? Environment.ProcessorCount : requested;

    public static void ForRows(int count, int maxDegreeOfParallelism, Action<int, int> rowRange)
    {
        var degree = Math.Min(DegreeOfParallelism(maxDegreeOfParallelism), Math.Max(1, count / 16));
        if (degree <= 1)
        {
            rowRange(0, count);
            return;
        }

        var band = (count + degree - 1) / degree;
        Parallel.For(0, degree, new ParallelOptions { MaxDegreeOfParallelism = degree }, index =>
        {
            var start = index * band;
            rowRange(start, Math.Min(count, start + band));
        });
    }

    public static void ApplyOwning(VideoFrame frame, Action<VideoFrame> emit, Func<VideoFrame, VideoFrame> transform)
    {
        VideoFrame output;
        try
        {
            output = transform(frame);
        }
        catch
        {
            frame.Dispose();
            throw;
        }

        if (!ReferenceEquals(output, frame))
        {
            frame.Dispose();
        }

        emit(output);
    }

    public static void RequireSameColors(VideoFrame first, VideoFrame second, string parameterName)
    {
        if (first.Range != second.Range || first.Matrix != second.Matrix)
        {
            throw new ArgumentException(
                $"The frames use different colours ({first.Matrix}/{first.Range} and {second.Matrix}/{second.Range}); convert one first",
                parameterName);
        }
    }

    public static void CopyPlane(VideoFrame source, VideoFrame destination, VideoPlane plane, int sourceX, int sourceY, int destinationX, int destinationY, int width, int height)
    {
        for (var row = 0; row < height; row++)
        {
            source.GetRow(plane, sourceY + row).Slice(sourceX, width).CopyTo(destination.GetRow(plane, destinationY + row).Slice(destinationX, width));
        }
    }
}
