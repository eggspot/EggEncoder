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

    public static int Sad16x16Average(ReadOnlySpan<byte> source, int sourceStride, ReadOnlySpan<byte> first, int firstStride, ReadOnlySpan<byte> second, int secondStride)
    {
        var total = Vector128<ushort>.Zero;
        for (var y = 0; y < 16; y++)
        {
            var a = Vector128.Create<byte>(first.Slice(y * firstStride, 16));
            var b = Vector128.Create<byte>(second.Slice(y * secondStride, 16));
            var average = (a | b) - Vector128.ShiftRightLogical(a ^ b, 1);
            var s = Vector128.Create<byte>(source.Slice(y * sourceStride, 16));
            var difference = Vector128.Max(s, average) - Vector128.Min(s, average);
            total += Vector128.WidenLower(difference) + Vector128.WidenUpper(difference);
        }

        return (int)Vector128.Sum(Vector128.WidenLower(total) + Vector128.WidenUpper(total));
    }

    public static int Satd(ReadOnlySpan<byte> source, int sourceStride, ReadOnlySpan<byte> prediction, int predictionStride, int width, int height)
    {
        if (width % 8 == 0)
        {
            var sum = Vector128<int>.Zero;
            for (var by = 0; by < height; by += 4)
            {
                for (var bx = 0; bx < width; bx += 8)
                {
                    sum += Satd8x4(source[((by * sourceStride) + bx)..], sourceStride, prediction[((by * predictionStride) + bx)..], predictionStride);
                }
            }

            return Vector128.Sum(sum) >> 1;
        }

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

    public static int Sa8d16x16(ReadOnlySpan<byte> source, int sourceStride, ReadOnlySpan<byte> prediction, int predictionStride)
    {
        var total = 0;
        Span<int> d = stackalloc int[64];
        for (var block = 0; block < 4; block++)
        {
            var bx = (block & 1) * 8;
            var by = (block >> 1) * 8;
            for (var y = 0; y < 8; y++)
            {
                for (var x = 0; x < 8; x++)
                {
                    d[(y * 8) + x] = source[((by + y) * sourceStride) + bx + x] - prediction[((by + y) * predictionStride) + bx + x];
                }
            }

            for (var y = 0; y < 8; y++)
            {
                Hadamard8(d.Slice(y * 8, 8), 1);
            }

            var sum = 0;
            for (var x = 0; x < 8; x++)
            {
                Hadamard8(d[x..], 8);
                for (var y = 0; y < 8; y++)
                {
                    sum += Math.Abs(d[x + (y * 8)]);
                }
            }

            total += (sum + 2) >> 2;
        }

        return total;
    }

    private static void Hadamard8(Span<int> v, int step)
    {
        for (var span = 1; span < 8; span <<= 1)
        {
            for (var i = 0; i < 8; i += span << 1)
            {
                for (var j = i; j < i + span; j++)
                {
                    var a = v[j * step];
                    var b = v[(j + span) * step];
                    v[j * step] = a + b;
                    v[(j + span) * step] = a - b;
                }
            }
        }
    }

    private static Vector128<int> Satd8x4(ReadOnlySpan<byte> source, int sourceStride, ReadOnlySpan<byte> prediction, int predictionStride)
    {
        var r0 = Difference(source, prediction);
        var r1 = Difference(source[sourceStride..], prediction[predictionStride..]);
        var r2 = Difference(source[(2 * sourceStride)..], prediction[(2 * predictionStride)..]);
        var r3 = Difference(source[(3 * sourceStride)..], prediction[(3 * predictionStride)..]);
        var a = r0 + r1;
        var b = r0 - r1;
        var c = r2 + r3;
        var d = r2 - r3;
        var total = Horizontal(a + c) + Horizontal(b + d) + Horizontal(a - c) + Horizontal(b - d);
        return Vector128.WidenLower(total).AsInt32() + Vector128.WidenUpper(total).AsInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Difference(ReadOnlySpan<byte> source, ReadOnlySpan<byte> prediction)
    {
        var a = Vector128.WidenLower(Vector128.CreateScalar(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(source)).AsByte()).AsInt16();
        var b = Vector128.WidenLower(Vector128.CreateScalar(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(prediction)).AsByte()).AsInt16();
        return a - b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> Horizontal(Vector128<short> v)
    {
        var even = Vector128.Create((short)-1, 0, -1, 0, -1, 0, -1, 0);
        var lowPairs = Vector128.Create((short)-1, -1, 0, 0, -1, -1, 0, 0);
        var swapped = Vector128.Shuffle(v, Vector128.Create((short)1, 0, 3, 2, 5, 4, 7, 6));
        var u = Vector128.ConditionalSelect(even, v + swapped, swapped - v);
        var pairs = Vector128.Shuffle(u, Vector128.Create((short)2, 3, 0, 1, 6, 7, 4, 5));
        var w = Vector128.ConditionalSelect(lowPairs, u + pairs, u - pairs);
        return Vector128.Abs(w).AsUInt16();
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
