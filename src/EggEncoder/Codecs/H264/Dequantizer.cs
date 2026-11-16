using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.InteropServices;
namespace EggEncoder.Codecs.H264;

internal sealed class Dequantizer
{
    private static readonly int[][] NormAdjust4x4 =
    [
        [10, 16, 13],
        [11, 18, 14],
        [13, 20, 16],
        [14, 23, 18],
        [16, 25, 20],
        [18, 29, 23]
    ];

    private static readonly int[][] NormAdjust8x8 =
    [
        [20, 18, 32, 19, 25, 24],
        [22, 19, 35, 21, 28, 26],
        [26, 23, 42, 24, 33, 31],
        [28, 25, 45, 26, 35, 33],
        [32, 28, 51, 30, 40, 38],
        [36, 32, 58, 34, 46, 43]
    ];

    private static readonly byte[] ChromaQpTable =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29,
        29, 30, 31, 32, 32, 33, 34, 34, 35, 35, 36, 36, 37, 37, 37, 38, 38, 38, 39, 39, 39, 39
    ];

    private readonly int[][][] _levelScale4x4 = new int[6][][];
    private readonly int[][][] _levelScale8x8 = new int[2][][];

    public Dequantizer(byte[][] scalingLists)
    {
        for (var list = 0; list < 6; list++)
        {
            _levelScale4x4[list] = new int[6][];
            for (var m = 0; m < 6; m++)
            {
                var scale = new int[16];
                for (var scan = 0; scan < 16; scan++)
                {
                    var raster = MacroblockLayout.ZigZag4x4[scan];
                    var row = raster >> 2;
                    var column = raster & 3;
                    var adjust = (row & 1) == 0 && (column & 1) == 0 ? NormAdjust4x4[m][0]
                        : (row & 1) == 1 && (column & 1) == 1 ? NormAdjust4x4[m][1]
                        : NormAdjust4x4[m][2];
                    scale[raster] = scalingLists[list][scan] * adjust;
                }

                _levelScale4x4[list][m] = scale;
            }
        }

        for (var list = 0; list < 2; list++)
        {
            _levelScale8x8[list] = new int[6][];
            for (var m = 0; m < 6; m++)
            {
                var scale = new int[64];
                for (var scan = 0; scan < 64; scan++)
                {
                    var raster = MacroblockLayout.ZigZag8x8[scan];
                    scale[raster] = scalingLists[6 + list][scan] * NormAdjust8x8[m][Position8x8(raster >> 3, raster & 7)];
                }

                _levelScale8x8[list][m] = scale;
            }
        }
    }

    public static int ChromaQp(int lumaQp, int offset) => ChromaQpTable[Math.Clamp(lumaQp + offset, 0, 51)];

    public void Dequantize4x4(ReadOnlySpan<short> levels, Span<int> output, int qp, int list, bool keepDc)
    {
        var dc = output[0];
        Scale(levels[..16], _levelScale4x4[list][qp % 6], output[..16], qp / 6, 4);
        if (keepDc)
        {
            output[0] = dc;
        }
    }

    public void Dequantize8x8(ReadOnlySpan<short> levels, Span<int> output, int qp, int list) =>
        Scale(levels[..64], _levelScale8x8[list][qp % 6], output[..64], qp / 6, 6);

    private static void Scale(ReadOnlySpan<short> levels, int[] scale, Span<int> output, int shift, int normalShift)
    {
        ref var levelStart = ref MemoryMarshal.GetReference(levels);
        ref var scaleStart = ref MemoryMarshal.GetArrayDataReference(scale);
        ref var outputStart = ref MemoryMarshal.GetReference(output);
        var count = levels.Length;
        if (shift >= normalShift)
        {
            var left = shift - normalShift;
            for (var i = 0; i < count; i += 8)
            {
                var level = Vector128.LoadUnsafe(ref levelStart, (nuint)i);
                var low = Vector128.WidenLower(level) * Vector128.LoadUnsafe(ref scaleStart, (nuint)i);
                var high = Vector128.WidenUpper(level) * Vector128.LoadUnsafe(ref scaleStart, (nuint)(i + 4));
                Vector128.ShiftLeft(low, left).StoreUnsafe(ref outputStart, (nuint)i);
                Vector128.ShiftLeft(high, left).StoreUnsafe(ref outputStart, (nuint)(i + 4));
            }

            return;
        }

        var right = normalShift - shift;
        var rounding = Vector128.Create(1 << (right - 1));
        for (var i = 0; i < count; i += 8)
        {
            var level = Vector128.LoadUnsafe(ref levelStart, (nuint)i);
            var low = (Vector128.WidenLower(level) * Vector128.LoadUnsafe(ref scaleStart, (nuint)i)) + rounding;
            var high = (Vector128.WidenUpper(level) * Vector128.LoadUnsafe(ref scaleStart, (nuint)(i + 4))) + rounding;
            Vector128.ShiftRightArithmetic(low, right).StoreUnsafe(ref outputStart, (nuint)i);
            Vector128.ShiftRightArithmetic(high, right).StoreUnsafe(ref outputStart, (nuint)(i + 4));
        }
    }

    public void LumaDc(ReadOnlySpan<short> levels, Span<int> output, int qp, int list)
    {
        Span<int> c = stackalloc int[16];
        for (var scan = 0; scan < 16; scan++)
        {
            c[MacroblockLayout.ZigZag4x4[scan]] = levels[scan];
        }

        Span<int> f = stackalloc int[16];
        Hadamard4x4(c, f);
        var scale = _levelScale4x4[list][qp % 6][0];
        var shift = qp / 6;
        for (var i = 0; i < 16; i++)
        {
            output[i] = shift >= 6 ? (f[i] * scale) << (shift - 6) : ((f[i] * scale) + (1 << (5 - shift))) >> (6 - shift);
        }
    }

    public void ChromaDc(ReadOnlySpan<short> levels, Span<int> output, int qp, int list)
    {
        var c0 = levels[0];
        var c1 = levels[1];
        var c2 = levels[2];
        var c3 = levels[3];
        var f0 = c0 + c1 + c2 + c3;
        var f1 = c0 - c1 + c2 - c3;
        var f2 = c0 + c1 - c2 - c3;
        var f3 = c0 - c1 - c2 + c3;
        var scale = _levelScale4x4[list][qp % 6][0];
        var shift = qp / 6;
        output[0] = ((f0 * scale) << shift) >> 5;
        output[1] = ((f1 * scale) << shift) >> 5;
        output[2] = ((f2 * scale) << shift) >> 5;
        output[3] = ((f3 * scale) << shift) >> 5;
    }

    private static void Hadamard4x4(ReadOnlySpan<int> c, Span<int> f)
    {
        Span<int> t = stackalloc int[16];
        for (var row = 0; row < 4; row++)
        {
            var a = c[row * 4];
            var b = c[(row * 4) + 1];
            var d = c[(row * 4) + 2];
            var e = c[(row * 4) + 3];
            t[row * 4] = a + b + d + e;
            t[(row * 4) + 1] = a + b - d - e;
            t[(row * 4) + 2] = a - b - d + e;
            t[(row * 4) + 3] = a - b + d - e;
        }

        for (var column = 0; column < 4; column++)
        {
            var a = t[column];
            var b = t[4 + column];
            var d = t[8 + column];
            var e = t[12 + column];
            f[column] = a + b + d + e;
            f[4 + column] = a + b - d - e;
            f[8 + column] = a - b - d + e;
            f[12 + column] = a - b + d - e;
        }
    }

    private static int Position8x8(int row, int column)
    {
        if ((row & 3) == 0 && (column & 3) == 0)
        {
            return 0;
        }

        if ((row & 1) == 1 && (column & 1) == 1)
        {
            return 1;
        }

        if ((row & 3) == 2 && (column & 3) == 2)
        {
            return 2;
        }

        if (((row & 3) == 0 && (column & 1) == 1) || ((row & 1) == 1 && (column & 3) == 0))
        {
            return 3;
        }

        if (((row & 3) == 0 && (column & 3) == 2) || ((row & 3) == 2 && (column & 3) == 0))
        {
            return 4;
        }

        return 5;
    }
}

internal static class InverseTransform
{
    public static void Add4x4(Span<int> d, byte[] plane, int offset, int stride)
    {
        if (Sse2.IsSupported || AdvSimd.Arm64.IsSupported)
        {
            Add4x4Vectors(d, plane, offset, stride);
            return;
        }

        for (var row = 0; row < 4; row++)
        {
            var r = row * 4;
            var e0 = d[r] + d[r + 2];
            var e1 = d[r] - d[r + 2];
            var e2 = (d[r + 1] >> 1) - d[r + 3];
            var e3 = d[r + 1] + (d[r + 3] >> 1);
            d[r] = e0 + e3;
            d[r + 1] = e1 + e2;
            d[r + 2] = e1 - e2;
            d[r + 3] = e0 - e3;
        }

        for (var column = 0; column < 4; column++)
        {
            var g0 = d[column] + d[8 + column];
            var g1 = d[column] - d[8 + column];
            var g2 = (d[4 + column] >> 1) - d[12 + column];
            var g3 = d[4 + column] + (d[12 + column] >> 1);
            Put(plane, offset + column, g0 + g3);
            Put(plane, offset + stride + column, g1 + g2);
            Put(plane, offset + (2 * stride) + column, g1 - g2);
            Put(plane, offset + (3 * stride) + column, g0 - g3);
        }
    }

    public static void AddDcOnly(int dc, byte[] plane, int offset, int stride, int size)
    {
        var value = (dc + 32) >> 6;
        if (value == 0)
        {
            return;
        }

        for (var y = 0; y < size; y++)
        {
            var row = offset + (y * stride);
            for (var x = 0; x < size; x++)
            {
                plane[row + x] = IntraPrediction.Clip(plane[row + x] + value);
            }
        }
    }

    public static void Add8x8(Span<int> d, byte[] plane, int offset, int stride)
    {
        if (Sse2.IsSupported || AdvSimd.Arm64.IsSupported)
        {
            Add8x8Vectors(d, plane, offset, stride);
            return;
        }

        for (var row = 0; row < 8; row++)
        {
            Transform8(d.Slice(row * 8, 8), 1);
        }

        Span<int> column = stackalloc int[8];
        for (var x = 0; x < 8; x++)
        {
            for (var y = 0; y < 8; y++)
            {
                column[y] = d[(y * 8) + x];
            }

            Transform8(column, 1);
            for (var y = 0; y < 8; y++)
            {
                Put(plane, offset + (y * stride) + x, column[y]);
            }
        }
    }

    private static void Transform8(Span<int> v, int step)
    {
        var a0 = v[0] + v[4];
        var a4 = v[0] - v[4];
        var a2 = (v[2] >> 1) - v[6];
        var a6 = v[2] + (v[6] >> 1);
        var b0 = a0 + a6;
        var b2 = a4 + a2;
        var b4 = a4 - a2;
        var b6 = a0 - a6;
        var a1 = -v[3] + v[5] - v[7] - (v[7] >> 1);
        var a3 = v[1] + v[7] - v[3] - (v[3] >> 1);
        var a5 = -v[1] + v[7] + v[5] + (v[5] >> 1);
        var a7 = v[3] + v[5] + v[1] + (v[1] >> 1);
        var b1 = a1 + (a7 >> 2);
        var b7 = a7 - (a1 >> 2);
        var b3 = a3 + (a5 >> 2);
        var b5 = (a3 >> 2) - a5;
        v[0] = b0 + b7;
        v[1] = b2 + b5;
        v[2] = b4 + b3;
        v[3] = b6 + b1;
        v[4] = b6 - b1;
        v[5] = b4 - b3;
        v[6] = b2 - b5;
        v[7] = b0 - b7;
    }

    private static void Add8x8Vectors(Span<int> d, byte[] plane, int offset, int stride)
    {
        ref var start = ref MemoryMarshal.GetReference(d[..64]);
        Span<Vector128<int>> low = stackalloc Vector128<int>[8];
        Span<Vector128<int>> high = stackalloc Vector128<int>[8];
        for (var row = 0; row < 8; row++)
        {
            low[row] = Vector128.LoadUnsafe(ref start, (nuint)(row * 8));
            high[row] = Vector128.LoadUnsafe(ref start, (nuint)((row * 8) + 4));
        }

        IntegerTransposes.Transpose8x8(low, high);
        Transform8Lanes(low);
        Transform8Lanes(high);
        IntegerTransposes.Transpose8x8(low, high);
        Transform8Lanes(low);
        Transform8Lanes(high);
        var rows = plane.AsSpan(offset, (7 * stride) + 8);
        for (var row = 0; row < 8; row++)
        {
            AddResidualRow(rows, row * stride, low[row]);
            AddResidualRow(rows, (row * stride) + 4, high[row]);
        }
    }

    private static void Transform8Lanes(Span<Vector128<int>> v)
    {
        var a0 = v[0] + v[4];
        var a4 = v[0] - v[4];
        var a2 = Vector128.ShiftRightArithmetic(v[2], 1) - v[6];
        var a6 = v[2] + Vector128.ShiftRightArithmetic(v[6], 1);
        var b0 = a0 + a6;
        var b2 = a4 + a2;
        var b4 = a4 - a2;
        var b6 = a0 - a6;
        var a1 = -v[3] + v[5] - v[7] - Vector128.ShiftRightArithmetic(v[7], 1);
        var a3 = v[1] + v[7] - v[3] - Vector128.ShiftRightArithmetic(v[3], 1);
        var a5 = -v[1] + v[7] + v[5] + Vector128.ShiftRightArithmetic(v[5], 1);
        var a7 = v[3] + v[5] + v[1] + Vector128.ShiftRightArithmetic(v[1], 1);
        var b1 = a1 + Vector128.ShiftRightArithmetic(a7, 2);
        var b7 = a7 - Vector128.ShiftRightArithmetic(a1, 2);
        var b3 = a3 + Vector128.ShiftRightArithmetic(a5, 2);
        var b5 = Vector128.ShiftRightArithmetic(a3, 2) - a5;
        v[0] = b0 + b7;
        v[1] = b2 + b5;
        v[2] = b4 + b3;
        v[3] = b6 + b1;
        v[4] = b6 - b1;
        v[5] = b4 - b3;
        v[6] = b2 - b5;
        v[7] = b0 - b7;
    }

    private static void Add4x4Vectors(Span<int> d, byte[] plane, int offset, int stride)
    {
        ref var start = ref MemoryMarshal.GetReference(d[..16]);
        var (c0, c1, c2, c3) = IntegerTransposes.Transpose4x4(
            Vector128.LoadUnsafe(ref start),
            Vector128.LoadUnsafe(ref start, 4),
            Vector128.LoadUnsafe(ref start, 8),
            Vector128.LoadUnsafe(ref start, 12));
        var e0 = c0 + c2;
        var e1 = c0 - c2;
        var e2 = Vector128.ShiftRightArithmetic(c1, 1) - c3;
        var e3 = c1 + Vector128.ShiftRightArithmetic(c3, 1);
        var (r0, r1, r2, r3) = IntegerTransposes.Transpose4x4(e0 + e3, e1 + e2, e1 - e2, e0 - e3);
        var g0 = r0 + r2;
        var g1 = r0 - r2;
        var g2 = Vector128.ShiftRightArithmetic(r1, 1) - r3;
        var g3 = r1 + Vector128.ShiftRightArithmetic(r3, 1);
        var rows = plane.AsSpan(offset, (3 * stride) + 4);
        AddResidualRow(rows, 0, g0 + g3);
        AddResidualRow(rows, stride, g1 + g2);
        AddResidualRow(rows, 2 * stride, g1 - g2);
        AddResidualRow(rows, 3 * stride, g0 - g3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddResidualRow(Span<byte> rows, int index, Vector128<int> residual)
    {
        ref var target = ref Unsafe.Add(ref MemoryMarshal.GetReference(rows), index);
        var prediction = Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalar(Unsafe.ReadUnaligned<uint>(ref target)).AsByte())).AsInt32();
        var sum = prediction + Vector128.ShiftRightArithmetic(residual + Vector128.Create(32), 6);
        var clamped = Vector128.Min(Vector128.Max(sum, Vector128<int>.Zero), Vector128.Create(255)).AsUInt32();
        var words = Vector128.Narrow(clamped, clamped);
        Unsafe.WriteUnaligned(ref target, Vector128.Narrow(words, words).AsUInt32().ToScalar());
    }

    private static void Put(byte[] plane, int index, int residual) =>
        plane[index] = IntraPrediction.Clip(plane[index] + ((residual + 32) >> 6));
}
