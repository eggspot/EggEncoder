using System.Runtime.CompilerServices;

namespace EggEncoder.Codecs.H264;

[SkipLocalsInit]
internal static class InterPrediction
{
    public static void Luma(H264Picture reference, int x, int y, int xFrac, int yFrac, int width, int height, Span<byte> output)
    {
        var frame = reference.Frame;
        var pictureWidth = reference.WidthInMacroblocks * 16;
        var pictureHeight = reference.HeightInMacroblocks * 16;
        scoped ReadOnlySpan<byte> source;
        int origin;
        int stride;
        Span<byte> fetched = stackalloc byte[21 * 21];
        if (x - 2 >= 0 && y - 2 >= 0 && x + width + 3 <= pictureWidth && y + height + 3 <= pictureHeight)
        {
            source = frame.Buffer;
            stride = frame.StrideY;
            origin = frame.OffsetY + (y * stride) + x;
        }
        else
        {
            stride = width + 5;
            Fetch(frame.Buffer, frame.OffsetY, frame.StrideY, pictureWidth, pictureHeight, x - 2, y - 2, stride, height + 5, fetched);
            source = fetched;
            origin = (2 * stride) + 2;
        }

        if (xFrac == 0 && yFrac == 0)
        {
            for (var j = 0; j < height; j++)
            {
                source.Slice(origin + (j * stride), width).CopyTo(output.Slice(j * width, width));
            }

            return;
        }

        if (yFrac == 0)
        {
            for (var j = 0; j < height; j++)
            {
                var row = origin + (j * stride);
                for (var i = 0; i < width; i++)
                {
                    var b = HalfHorizontal(source, row + i);
                    output[(j * width) + i] = (byte)(xFrac == 2 ? b : (b + source[row + i + (xFrac >> 1)] + 1) >> 1);
                }
            }

            return;
        }

        if (xFrac == 0)
        {
            for (var j = 0; j < height; j++)
            {
                var row = origin + (j * stride);
                for (var i = 0; i < width; i++)
                {
                    var h = HalfVertical(source, row + i, stride);
                    output[(j * width) + i] = (byte)(yFrac == 2 ? h : (h + source[row + i + ((yFrac >> 1) * stride)] + 1) >> 1);
                }
            }

            return;
        }

        if (xFrac != 2 && yFrac != 2)
        {
            var bRow = yFrac >> 1;
            var hColumn = xFrac >> 1;
            for (var j = 0; j < height; j++)
            {
                var row = origin + (j * stride);
                for (var i = 0; i < width; i++)
                {
                    var b = HalfHorizontal(source, row + (bRow * stride) + i);
                    var h = HalfVertical(source, row + i + hColumn, stride);
                    output[(j * width) + i] = (byte)((b + h + 1) >> 1);
                }
            }

            return;
        }

        Span<int> intermediate = stackalloc int[16 * 21];
        for (var j = -2; j < height + 3; j++)
        {
            var row = origin + (j * stride);
            for (var i = 0; i < width; i++)
            {
                intermediate[((j + 2) * width) + i] = Tap6(source, row + i, 1);
            }
        }

        for (var j = 0; j < height; j++)
        {
            var row = origin + (j * stride);
            for (var i = 0; i < width; i++)
            {
                var index = ((j + 2) * width) + i;
                var j1 = intermediate[index - (2 * width)] - (5 * intermediate[index - width]) + (20 * intermediate[index])
                    + (20 * intermediate[index + width]) - (5 * intermediate[index + (2 * width)]) + intermediate[index + (3 * width)];
                var center = IntraPrediction.Clip((j1 + 512) >> 10);
                int value;
                if (xFrac == 2 && yFrac == 2)
                {
                    value = center;
                }
                else if (xFrac == 2)
                {
                    var b = IntraPrediction.Clip((intermediate[index + ((yFrac >> 1) * width)] + 16) >> 5);
                    value = (b + center + 1) >> 1;
                }
                else
                {
                    var h = HalfVertical(source, row + i + (xFrac >> 1), stride);
                    value = (h + center + 1) >> 1;
                }

                output[(j * width) + i] = (byte)value;
            }
        }
    }

    public static void Chroma(H264Picture reference, bool cr, int x, int y, int xFrac, int yFrac, int width, int height, Span<byte> output)
    {
        var frame = reference.Frame;
        var pictureWidth = reference.WidthInMacroblocks * 8;
        var pictureHeight = reference.HeightInMacroblocks * 8;
        var planeOffset = cr ? frame.OffsetV : frame.OffsetU;
        scoped ReadOnlySpan<byte> source;
        int origin;
        int stride;
        Span<byte> fetched = stackalloc byte[9 * 9];
        if (x >= 0 && y >= 0 && x + width + 1 <= pictureWidth && y + height + 1 <= pictureHeight)
        {
            source = frame.Buffer;
            stride = frame.StrideUV;
            origin = planeOffset + (y * stride) + x;
        }
        else
        {
            stride = width + 1;
            Fetch(frame.Buffer, planeOffset, frame.StrideUV, pictureWidth, pictureHeight, x, y, stride, height + 1, fetched);
            source = fetched;
            origin = 0;
        }

        if (xFrac == 0 && yFrac == 0)
        {
            for (var j = 0; j < height; j++)
            {
                source.Slice(origin + (j * stride), width).CopyTo(output.Slice(j * width, width));
            }

            return;
        }

        var a = (8 - xFrac) * (8 - yFrac);
        var b = xFrac * (8 - yFrac);
        var c = (8 - xFrac) * yFrac;
        var d = xFrac * yFrac;
        for (var j = 0; j < height; j++)
        {
            var row = origin + (j * stride);
            for (var i = 0; i < width; i++)
            {
                var index = row + i;
                output[(j * width) + i] = (byte)(((a * source[index]) + (b * source[index + 1]) + (c * source[index + stride]) + (d * source[index + stride + 1]) + 32) >> 6);
            }
        }
    }

    public static void WeightExplicit(Span<byte> samples, int log2Denominator, int weight, int offset)
    {
        if (log2Denominator >= 1)
        {
            var round = 1 << (log2Denominator - 1);
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = IntraPrediction.Clip((((samples[i] * weight) + round) >> log2Denominator) + offset);
            }
        }
        else
        {
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = IntraPrediction.Clip((samples[i] * weight) + offset);
            }
        }
    }

    public static void Bipredict(Span<byte> first, ReadOnlySpan<byte> second, int log2Denominator, int weight0, int weight1, int offset0, int offset1)
    {
        var offset = (offset0 + offset1 + 1) >> 1;
        var round = 1 << log2Denominator;
        for (var i = 0; i < first.Length; i++)
        {
            first[i] = IntraPrediction.Clip((((first[i] * weight0) + (second[i] * weight1) + round) >> (log2Denominator + 1)) + offset);
        }
    }

    public static void AverageInto(Span<byte> first, ReadOnlySpan<byte> second)
    {
        for (var i = 0; i < first.Length; i++)
        {
            first[i] = (byte)((first[i] + second[i] + 1) >> 1);
        }
    }

    private static void Fetch(byte[] buffer, int offset, int stride, int pictureWidth, int pictureHeight, int x, int y, int width, int height, Span<byte> output)
    {
        for (var j = 0; j < height; j++)
        {
            var row = offset + (Math.Clamp(y + j, 0, pictureHeight - 1) * stride);
            for (var i = 0; i < width; i++)
            {
                output[(j * width) + i] = buffer[row + Math.Clamp(x + i, 0, pictureWidth - 1)];
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Tap6(ReadOnlySpan<byte> source, int index, int step) =>
        source[index - (2 * step)] - (5 * source[index - step]) + (20 * source[index]) + (20 * source[index + step]) - (5 * source[index + (2 * step)]) + source[index + (3 * step)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HalfHorizontal(ReadOnlySpan<byte> source, int index) => IntraPrediction.Clip((Tap6(source, index, 1) + 16) >> 5);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HalfVertical(ReadOnlySpan<byte> source, int index, int stride) => IntraPrediction.Clip((Tap6(source, index, stride) + 16) >> 5);
}
