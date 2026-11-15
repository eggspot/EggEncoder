using System.Runtime.CompilerServices;

namespace EggEncoder.Codecs.H264;

[SkipLocalsInit]
internal static class ForwardTransform
{
    public static void Core4x4(ReadOnlySpan<int> residual, Span<int> coefficients)
    {
        Span<int> rows = stackalloc int[16];
        for (var i = 0; i < 4; i++)
        {
            var x0 = residual[(i * 4) + 0];
            var x1 = residual[(i * 4) + 1];
            var x2 = residual[(i * 4) + 2];
            var x3 = residual[(i * 4) + 3];
            var s03 = x0 + x3;
            var d03 = x0 - x3;
            var s12 = x1 + x2;
            var d12 = x1 - x2;
            rows[(i * 4) + 0] = s03 + s12;
            rows[(i * 4) + 1] = (2 * d03) + d12;
            rows[(i * 4) + 2] = s03 - s12;
            rows[(i * 4) + 3] = d03 - (2 * d12);
        }

        for (var j = 0; j < 4; j++)
        {
            var x0 = rows[j];
            var x1 = rows[4 + j];
            var x2 = rows[8 + j];
            var x3 = rows[12 + j];
            var s03 = x0 + x3;
            var d03 = x0 - x3;
            var s12 = x1 + x2;
            var d12 = x1 - x2;
            coefficients[j] = s03 + s12;
            coefficients[4 + j] = (2 * d03) + d12;
            coefficients[8 + j] = s03 - s12;
            coefficients[12 + j] = d03 - (2 * d12);
        }
    }

    public static void Hadamard4x4(Span<int> values)
    {
        Span<int> rows = stackalloc int[16];
        for (var i = 0; i < 4; i++)
        {
            var a = values[(i * 4) + 0];
            var b = values[(i * 4) + 1];
            var c = values[(i * 4) + 2];
            var d = values[(i * 4) + 3];
            rows[(i * 4) + 0] = a + b + c + d;
            rows[(i * 4) + 1] = a + b - c - d;
            rows[(i * 4) + 2] = a - b - c + d;
            rows[(i * 4) + 3] = a - b + c - d;
        }

        for (var j = 0; j < 4; j++)
        {
            var a = rows[j];
            var b = rows[4 + j];
            var c = rows[8 + j];
            var d = rows[12 + j];
            values[j] = (a + b + c + d) >> 1;
            values[4 + j] = (a + b - c - d) >> 1;
            values[8 + j] = (a - b - c + d) >> 1;
            values[12 + j] = (a - b + c - d) >> 1;
        }
    }

    public static void Hadamard2x2(Span<int> values)
    {
        var a = values[0];
        var b = values[1];
        var c = values[2];
        var d = values[3];
        values[0] = a + b + c + d;
        values[1] = a - b + c - d;
        values[2] = a + b - c - d;
        values[3] = a - b - c + d;
    }
}

internal static class Quantizer
{
    private static readonly int[][] Factors =
    [
        [13107, 5243, 8066],
        [11916, 4660, 7490],
        [10082, 4194, 6554],
        [9362, 3647, 5825],
        [8192, 3355, 5243],
        [7282, 2893, 4559]
    ];

    public static int Quantize4x4(ReadOnlySpan<int> coefficients, Span<short> raster, Span<short> scanned, int qp, bool intra, int first)
    {
        var shift = 15 + (qp / 6);
        var rounding = intra ? (1 << shift) / 3 : (1 << shift) / 6;
        var factors = Factors[qp % 6];
        var nonzero = 0;
        for (var scan = first; scan < 16; scan++)
        {
            var position = MacroblockLayout.ZigZag4x4[scan];
            var row = position >> 2;
            var column = position & 3;
            var factor = (row & 1) == 0 && (column & 1) == 0 ? factors[0] : (row & 1) == 1 && (column & 1) == 1 ? factors[1] : factors[2];
            var value = coefficients[position];
            var level = ((Math.Abs(value) * (long)factor) + rounding) >> shift;
            var signed = (short)(value < 0 ? -level : level);
            raster[position] = signed;
            scanned[scan - first] = signed;
            if (signed != 0)
            {
                nonzero++;
            }
        }

        if (first == 1)
        {
            raster[0] = 0;
        }

        return nonzero;
    }

    public static int QuantizeDc(ReadOnlySpan<int> values, Span<short> levels, int qp, bool intra)
    {
        var shift = 16 + (qp / 6);
        var rounding = intra ? (2 << (shift - 1)) / 3 : (2 << (shift - 1)) / 6;
        var factor = Factors[qp % 6][0];
        var nonzero = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            var level = ((Math.Abs(value) * (long)factor) + rounding) >> shift;
            levels[i] = (short)(value < 0 ? -level : level);
            if (level != 0)
            {
                nonzero++;
            }
        }

        return nonzero;
    }
}
