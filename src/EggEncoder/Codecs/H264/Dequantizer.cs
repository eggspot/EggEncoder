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
        var scale = _levelScale4x4[list][qp % 6];
        var shift = qp / 6;
        for (var i = keepDc ? 1 : 0; i < 16; i++)
        {
            int level = levels[i];
            if (level == 0)
            {
                output[i] = 0;
                continue;
            }

            output[i] = shift >= 4
                ? (level * scale[i]) << (shift - 4)
                : ((level * scale[i]) + (1 << (3 - shift))) >> (4 - shift);
        }
    }

    public void Dequantize8x8(ReadOnlySpan<short> levels, Span<int> output, int qp, int list)
    {
        var scale = _levelScale8x8[list][qp % 6];
        var shift = qp / 6;
        for (var i = 0; i < 64; i++)
        {
            int level = levels[i];
            output[i] = level == 0 ? 0
                : shift >= 6 ? (level * scale[i]) << (shift - 6)
                : ((level * scale[i]) + (1 << (5 - shift))) >> (6 - shift);
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

    private static void Put(byte[] plane, int index, int residual) =>
        plane[index] = IntraPrediction.Clip(plane[index] + ((residual + 32) >> 6));
}
