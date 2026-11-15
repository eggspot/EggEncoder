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

    public static void Core8x8(ReadOnlySpan<int> residual, Span<int> coefficients)
    {
        Span<int> rows = stackalloc int[64];
        for (var i = 0; i < 8; i++)
        {
            Butterfly8(residual.Slice(i * 8, 8), 1, rows.Slice(i * 8, 8), 1);
        }

        Span<int> column = stackalloc int[8];
        Span<int> output = stackalloc int[8];
        for (var j = 0; j < 8; j++)
        {
            for (var i = 0; i < 8; i++)
            {
                column[i] = rows[(i * 8) + j];
            }

            Butterfly8(column, 1, output, 1);
            for (var i = 0; i < 8; i++)
            {
                coefficients[(i * 8) + j] = output[i];
            }
        }
    }

    private static void Butterfly8(ReadOnlySpan<int> x, int inputStep, Span<int> y, int outputStep)
    {
        var a0 = x[0] + x[7];
        var a1 = x[inputStep] + x[6 * inputStep];
        var a2 = x[2 * inputStep] + x[5 * inputStep];
        var a3 = x[3 * inputStep] + x[4 * inputStep];
        var a4 = x[0] - x[7 * inputStep];
        var a5 = x[inputStep] - x[6 * inputStep];
        var a6 = x[2 * inputStep] - x[5 * inputStep];
        var a7 = x[3 * inputStep] - x[4 * inputStep];
        var b0 = a0 + a3;
        var b1 = a1 + a2;
        var b2 = a0 - a3;
        var b3 = a1 - a2;
        var b4 = a5 + a6 + ((a4 >> 1) + a4);
        var b5 = a4 - a7 - ((a6 >> 1) + a6);
        var b6 = a4 + a7 - ((a5 >> 1) + a5);
        var b7 = a5 - a6 + ((a7 >> 1) + a7);
        y[0] = b0 + b1;
        y[4 * outputStep] = b0 - b1;
        y[2 * outputStep] = b2 + (b3 >> 1);
        y[6 * outputStep] = (b2 >> 1) - b3;
        y[outputStep] = b4 + (b7 >> 2);
        y[7 * outputStep] = (b4 >> 2) - b7;
        y[3 * outputStep] = b5 + (b6 >> 2);
        y[5 * outputStep] = b6 - (b5 >> 2);
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

    private static readonly int[][] Factors8x8 =
    [
        [13107, 11428, 20972, 12222, 16777, 15481],
        [11916, 10826, 19174, 11058, 14980, 14290],
        [10082, 8943, 15978, 9675, 12710, 11985],
        [9362, 8228, 14913, 8931, 11984, 11259],
        [8192, 7346, 13159, 7740, 10486, 9777],
        [7282, 6428, 11570, 6830, 9118, 8640]
    ];

    public static int Quantize8x8(ReadOnlySpan<int> coefficients, Span<short> raster, Span<short> scanned, int qp, bool intra)
    {
        var shift = 16 + (qp / 6);
        var rounding = intra ? (1 << shift) / 3 : (1 << shift) / 6;
        var factors = Factors8x8[qp % 6];
        var nonzero = 0;
        for (var scan = 0; scan < 64; scan++)
        {
            var position = MacroblockLayout.ZigZag8x8[scan];
            var value = coefficients[position];
            var level = ((Math.Abs(value) * (long)factors[Class8x8(position >> 3, position & 7)]) + rounding) >> shift;
            var signed = (short)(value < 0 ? -level : level);
            raster[position] = signed;
            scanned[scan] = signed;
            if (signed != 0)
            {
                nonzero++;
            }
        }

        return nonzero;
    }

    private static int Class8x8(int row, int column)
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

    public static int DcLevel(int coefficient, int qp, bool intra)
    {
        var shift = 15 + (qp / 6);
        var rounding = intra ? (1 << shift) / 3 : (1 << shift) / 6;
        return (int)(((Math.Abs(coefficient) * (long)Factors[qp % 6][0]) + rounding) >> shift);
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
