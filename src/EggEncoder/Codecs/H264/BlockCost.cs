using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace EggEncoder.Codecs.H264;

[SkipLocalsInit]
internal static class BlockCost
{
    public static int Sad16x16(ReadOnlySpan<byte> source, int sourceStride, ReadOnlySpan<byte> other, int otherStride)
    {
        var total = Vector128<ushort>.Zero;
        for (var y = 0; y < 16; y++)
        {
            var a = Vector128.Create<byte>(source.Slice(y * sourceStride, 16));
            var b = Vector128.Create<byte>(other.Slice(y * otherStride, 16));
            var difference = Vector128.Max(a, b) - Vector128.Min(a, b);
            total += Vector128.WidenLower(difference) + Vector128.WidenUpper(difference);
        }

        return (int)Vector128.Sum(Vector128.WidenLower(total) + Vector128.WidenUpper(total));
    }

    public static int Satd(ReadOnlySpan<byte> source, int sourceStride, ReadOnlySpan<byte> prediction, int predictionStride, int width, int height)
    {
        var total = 0;
        Span<int> difference = stackalloc int[16];
        for (var by = 0; by < height; by += 4)
        {
            for (var bx = 0; bx < width; bx += 4)
            {
                for (var y = 0; y < 4; y++)
                {
                    var sourceRow = source.Slice(((by + y) * sourceStride) + bx, 4);
                    var predictionRow = prediction.Slice(((by + y) * predictionStride) + bx, 4);
                    for (var x = 0; x < 4; x++)
                    {
                        difference[(y * 4) + x] = sourceRow[x] - predictionRow[x];
                    }
                }

                total += Hadamard4x4Sum(difference);
            }
        }

        return total >> 1;
    }

    private static int Hadamard4x4Sum(Span<int> d)
    {
        Span<int> rows = stackalloc int[16];
        for (var i = 0; i < 4; i++)
        {
            var a = d[(i * 4) + 0] + d[(i * 4) + 1];
            var b = d[(i * 4) + 0] - d[(i * 4) + 1];
            var c = d[(i * 4) + 2] + d[(i * 4) + 3];
            var e = d[(i * 4) + 2] - d[(i * 4) + 3];
            rows[(i * 4) + 0] = a + c;
            rows[(i * 4) + 1] = b + e;
            rows[(i * 4) + 2] = a - c;
            rows[(i * 4) + 3] = b - e;
        }

        var sum = 0;
        for (var j = 0; j < 4; j++)
        {
            var a = rows[j] + rows[4 + j];
            var b = rows[j] - rows[4 + j];
            var c = rows[8 + j] + rows[12 + j];
            var e = rows[8 + j] - rows[12 + j];
            sum += Math.Abs(a + c) + Math.Abs(b + e) + Math.Abs(a - c) + Math.Abs(b - e);
        }

        return sum;
    }
}
