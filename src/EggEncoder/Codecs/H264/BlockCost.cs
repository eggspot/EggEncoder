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

    public static void Sad4x4Grid16x16(ReadOnlySpan<byte> source, int sourceStride, ReadOnlySpan<byte> other, int otherStride, Span<int> sads)
    {
        for (var row = 0; row < 4; row++)
        {
            var low = Vector128<ushort>.Zero;
            var high = Vector128<ushort>.Zero;
            for (var y = row * 4; y < (row * 4) + 4; y++)
            {
                var a = Vector128.Create<byte>(source.Slice(y * sourceStride, 16));
                var b = Vector128.Create<byte>(other.Slice(y * otherStride, 16));
                var difference = Vector128.Max(a, b) - Vector128.Min(a, b);
                low += Vector128.WidenLower(difference);
                high += Vector128.WidenUpper(difference);
            }

            sads[row * 4] = (int)Vector128.Sum(Vector128.WidenLower(low));
            sads[(row * 4) + 1] = (int)Vector128.Sum(Vector128.WidenUpper(low));
            sads[(row * 4) + 2] = (int)Vector128.Sum(Vector128.WidenLower(high));
            sads[(row * 4) + 3] = (int)Vector128.Sum(Vector128.WidenUpper(high));
        }
    }

    public static void Sad4x4Grid8x8(ReadOnlySpan<byte> source, int sourceStride, ReadOnlySpan<byte> other, int otherStride, Span<int> sads)
    {
        for (var row = 0; row < 2; row++)
        {
            var sum = Vector128<short>.Zero;
            for (var y = row * 4; y < (row * 4) + 4; y++)
            {
                sum += Vector128.Abs(Difference(source[(y * sourceStride)..], other[(y * otherStride)..]));
            }

            sads[row * 2] = Vector128.Sum(Vector128.WidenLower(sum));
            sads[(row * 2) + 1] = Vector128.Sum(Vector128.WidenUpper(sum));
        }
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

        var total = Vector128<int>.Zero;
        for (var by = 0; by < height; by += 4)
        {
            for (var bx = 0; bx < width; bx += 4)
            {
                total += Satd4x4Lanes(source[((by * sourceStride) + bx)..], sourceStride, prediction[((by * predictionStride) + bx)..], predictionStride);
            }
        }

        return Vector128.Sum(total) >> 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Satd4x4Lanes(ReadOnlySpan<byte> source, int sourceStride, ReadOnlySpan<byte> prediction, int predictionStride)
    {
        var d0 = DifferenceRow(source, 0, prediction, 0);
        var d1 = DifferenceRow(source, sourceStride, prediction, predictionStride);
        var d2 = DifferenceRow(source, 2 * sourceStride, prediction, 2 * predictionStride);
        var d3 = DifferenceRow(source, 3 * sourceStride, prediction, 3 * predictionStride);
        var a = d0 + d1;
        var b = d0 - d1;
        var c = d2 + d3;
        var e = d2 - d3;
        return Vector128.Abs(HadamardLanes(a + c)) + Vector128.Abs(HadamardLanes(b + e)) + Vector128.Abs(HadamardLanes(a - c)) + Vector128.Abs(HadamardLanes(b - e));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> DifferenceRow(ReadOnlySpan<byte> source, int sourceIndex, ReadOnlySpan<byte> prediction, int predictionIndex)
    {
        var s = Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalar(Unsafe.ReadUnaligned<uint>(ref Unsafe.AsRef(in source[sourceIndex]))).AsByte()));
        var p = Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalar(Unsafe.ReadUnaligned<uint>(ref Unsafe.AsRef(in prediction[predictionIndex]))).AsByte()));
        return s.AsInt32() - p.AsInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> HadamardLanes(Vector128<int> x)
    {
        var oddLanes = Vector128.Create(0, -1, 0, -1);
        var upperLanes = Vector128.Create(0, 0, -1, -1);
        var pairs = ((x ^ oddLanes) - oddLanes) + Vector128.Shuffle(x, Vector128.Create(1, 0, 3, 2));
        return ((pairs ^ upperLanes) - upperLanes) + Vector128.Shuffle(pairs, Vector128.Create(2, 3, 0, 1));
    }

    public static int Sa8d16x16(ReadOnlySpan<byte> source, int sourceStride, ReadOnlySpan<byte> prediction, int predictionStride)
    {
        var total = 0;
        for (var block = 0; block < 4; block++)
        {
            var bx = (block & 1) * 8;
            var by = (block >> 1) * 8;
            var sum = Sa8d8x8(source[((by * sourceStride) + bx)..], sourceStride, prediction[((by * predictionStride) + bx)..], predictionStride);
            total += (sum + 2) >> 2;
        }

        return total;
    }

    private static int Sa8d8x8(ReadOnlySpan<byte> source, int sourceStride, ReadOnlySpan<byte> prediction, int predictionStride)
    {
        var r0 = Difference(source, prediction);
        var r1 = Difference(source[sourceStride..], prediction[predictionStride..]);
        var r2 = Difference(source[(2 * sourceStride)..], prediction[(2 * predictionStride)..]);
        var r3 = Difference(source[(3 * sourceStride)..], prediction[(3 * predictionStride)..]);
        var r4 = Difference(source[(4 * sourceStride)..], prediction[(4 * predictionStride)..]);
        var r5 = Difference(source[(5 * sourceStride)..], prediction[(5 * predictionStride)..]);
        var r6 = Difference(source[(6 * sourceStride)..], prediction[(6 * predictionStride)..]);
        var r7 = Difference(source[(7 * sourceStride)..], prediction[(7 * predictionStride)..]);
        var a0 = r0 + r1;
        var a1 = r0 - r1;
        var a2 = r2 + r3;
        var a3 = r2 - r3;
        var a4 = r4 + r5;
        var a5 = r4 - r5;
        var a6 = r6 + r7;
        var a7 = r6 - r7;
        var b0 = a0 + a2;
        var b1 = a1 + a3;
        var b2 = a0 - a2;
        var b3 = a1 - a3;
        var b4 = a4 + a6;
        var b5 = a5 + a7;
        var b6 = a4 - a6;
        var b7 = a5 - a7;
        var magnitudes = Horizontal8(b0 + b4) + Horizontal8(b1 + b5) + Horizontal8(b2 + b6) + Horizontal8(b3 + b7)
            + Horizontal8(b0 - b4) + Horizontal8(b1 - b5) + Horizontal8(b2 - b6) + Horizontal8(b3 - b7);
        return (int)Vector128.Sum(magnitudes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> Horizontal8(Vector128<short> v)
    {
        var oddLanes = Vector128.Create((short)0, -1, 0, -1, 0, -1, 0, -1);
        var secondPairs = Vector128.Create((short)0, 0, -1, -1, 0, 0, -1, -1);
        var upperHalf = Vector128.Create((short)0, 0, 0, 0, -1, -1, -1, -1);
        var x = ((v ^ oddLanes) - oddLanes) + Vector128.Shuffle(v, Vector128.Create((short)1, 0, 3, 2, 5, 4, 7, 6));
        var y = ((x ^ secondPairs) - secondPairs) + Vector128.Shuffle(x, Vector128.Create((short)2, 3, 0, 1, 6, 7, 4, 5));
        var z = ((y ^ upperHalf) - upperHalf) + Vector128.Shuffle(y, Vector128.Create((short)4, 5, 6, 7, 0, 1, 2, 3));
        var magnitude = Vector128.Abs(z).AsUInt16();
        return Vector128.WidenLower(magnitude) + Vector128.WidenUpper(magnitude);
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
}
