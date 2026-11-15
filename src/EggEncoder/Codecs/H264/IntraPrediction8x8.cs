namespace EggEncoder.Codecs.H264;

internal static class IntraPrediction8x8
{
    public static void Predict(byte[] plane, int offset, int stride, int mode, bool left, bool top, bool topLeft, bool topRight)
    {
        Span<int> rawTop = stackalloc int[16];
        Span<int> rawLeft = stackalloc int[8];
        Span<int> filteredTop = stackalloc int[16];
        Span<int> filteredLeft = stackalloc int[8];
        var rawCorner = topLeft ? plane[offset - stride - 1] : 0;
        if (top)
        {
            for (var x = 0; x < 8; x++)
            {
                rawTop[x] = plane[offset - stride + x];
            }

            for (var x = 8; x < 16; x++)
            {
                rawTop[x] = topRight ? plane[offset - stride + x] : rawTop[7];
            }

            filteredTop[0] = topLeft ? (rawCorner + (2 * rawTop[0]) + rawTop[1] + 2) >> 2 : ((3 * rawTop[0]) + rawTop[1] + 2) >> 2;
            for (var x = 1; x < 15; x++)
            {
                filteredTop[x] = (rawTop[x - 1] + (2 * rawTop[x]) + rawTop[x + 1] + 2) >> 2;
            }

            filteredTop[15] = (rawTop[14] + (3 * rawTop[15]) + 2) >> 2;
        }

        if (left)
        {
            for (var y = 0; y < 8; y++)
            {
                rawLeft[y] = plane[offset + (y * stride) - 1];
            }

            filteredLeft[0] = topLeft ? (rawCorner + (2 * rawLeft[0]) + rawLeft[1] + 2) >> 2 : ((3 * rawLeft[0]) + rawLeft[1] + 2) >> 2;
            for (var y = 1; y < 7; y++)
            {
                filteredLeft[y] = (rawLeft[y - 1] + (2 * rawLeft[y]) + rawLeft[y + 1] + 2) >> 2;
            }

            filteredLeft[7] = (rawLeft[6] + (3 * rawLeft[7]) + 2) >> 2;
        }

        var corner = rawCorner;
        if (topLeft)
        {
            corner = top && left ? (rawTop[0] + (2 * rawCorner) + rawLeft[0] + 2) >> 2
                : top ? ((3 * rawCorner) + rawTop[0] + 2) >> 2
                : left ? ((3 * rawCorner) + rawLeft[0] + 2) >> 2
                : rawCorner;
        }

        var edges = new Edges(filteredTop, filteredLeft, corner);
        switch (mode)
        {
            case 0:
                Require(top, "Intra_8x8_Vertical");
                Fill(plane, offset, stride, (x, _, e) => e.Top[x], edges);
                break;
            case 1:
                Require(left, "Intra_8x8_Horizontal");
                Fill(plane, offset, stride, (_, y, e) => e.Left[y], edges);
                break;
            case 2:
                PredictDc(plane, offset, stride, top, left, filteredTop, filteredLeft);
                break;
            case 3:
                Require(top, "Intra_8x8_Diagonal_Down_Left");
                Fill(plane, offset, stride, static (x, y, e) => x == 7 && y == 7
                    ? (e.Top[14] + (3 * e.Top[15]) + 2) >> 2
                    : (e.Top[x + y] + (2 * e.Top[x + y + 1]) + e.Top[x + y + 2] + 2) >> 2, edges);
                break;
            case 4:
                Require(top && left && topLeft, "Intra_8x8_Diagonal_Down_Right");
                Fill(plane, offset, stride, static (x, y, e) => x > y
                    ? (e.T(x - y - 2) + (2 * e.T(x - y - 1)) + e.T(x - y) + 2) >> 2
                    : x < y
                        ? (e.L(y - x - 2) + (2 * e.L(y - x - 1)) + e.L(y - x) + 2) >> 2
                        : (e.T(0) + (2 * e.Corner) + e.L(0) + 2) >> 2, edges);
                break;
            case 5:
                Require(top && left && topLeft, "Intra_8x8_Vertical_Right");
                Fill(plane, offset, stride, static (x, y, e) => VerticalRight(x, y, e), edges);
                break;
            case 6:
                Require(top && left && topLeft, "Intra_8x8_Horizontal_Down");
                Fill(plane, offset, stride, static (x, y, e) => HorizontalDown(x, y, e), edges);
                break;
            case 7:
                Require(top, "Intra_8x8_Vertical_Left");
                Fill(plane, offset, stride, static (x, y, e) => (y & 1) == 0
                    ? (e.Top[x + (y >> 1)] + e.Top[x + (y >> 1) + 1] + 1) >> 1
                    : (e.Top[x + (y >> 1)] + (2 * e.Top[x + (y >> 1) + 1]) + e.Top[x + (y >> 1) + 2] + 2) >> 2, edges);
                break;
            case 8:
                Require(left, "Intra_8x8_Horizontal_Up");
                Fill(plane, offset, stride, static (x, y, e) => HorizontalUp(x, y, e), edges);
                break;
            default:
                throw new InvalidDataException("H.264: an Intra_8x8 prediction mode is out of range");
        }
    }

    private static int VerticalRight(int x, int y, Edges e)
    {
        var z = (2 * x) - y;
        var i = x - (y >> 1);
        return z switch
        {
            >= 0 when (z & 1) == 0 => (e.T(i - 1) + e.T(i) + 1) >> 1,
            > 0 => (e.T(i - 2) + (2 * e.T(i - 1)) + e.T(i) + 2) >> 2,
            -1 => (e.L(0) + (2 * e.Corner) + e.T(0) + 2) >> 2,
            _ => (e.L(y - (2 * x) - 1) + (2 * e.L(y - (2 * x) - 2)) + e.L(y - (2 * x) - 3) + 2) >> 2
        };
    }

    private static int HorizontalDown(int x, int y, Edges e)
    {
        var z = (2 * y) - x;
        var i = y - (x >> 1);
        return z switch
        {
            >= 0 when (z & 1) == 0 => (e.L(i - 1) + e.L(i) + 1) >> 1,
            > 0 => (e.L(i - 2) + (2 * e.L(i - 1)) + e.L(i) + 2) >> 2,
            -1 => (e.L(0) + (2 * e.Corner) + e.T(0) + 2) >> 2,
            _ => (e.T(x - (2 * y) - 1) + (2 * e.T(x - (2 * y) - 2)) + e.T(x - (2 * y) - 3) + 2) >> 2
        };
    }

    private static int HorizontalUp(int x, int y, Edges e)
    {
        var z = x + (2 * y);
        var i = y + (x >> 1);
        return z switch
        {
            > 13 => e.Left[7],
            13 => (e.Left[6] + (3 * e.Left[7]) + 2) >> 2,
            _ when (z & 1) == 0 => (e.Left[i] + e.Left[i + 1] + 1) >> 1,
            _ => (e.Left[i] + (2 * e.Left[i + 1]) + e.Left[i + 2] + 2) >> 2
        };
    }

    private static void PredictDc(byte[] plane, int offset, int stride, bool top, bool left, ReadOnlySpan<int> filteredTop, ReadOnlySpan<int> filteredLeft)
    {
        var sumTop = 0;
        var sumLeft = 0;
        for (var i = 0; i < 8; i++)
        {
            sumTop += filteredTop[i];
            sumLeft += filteredLeft[i];
        }

        var value = top && left ? (sumTop + sumLeft + 8) >> 4
            : left ? (sumLeft + 4) >> 3
            : top ? (sumTop + 4) >> 3
            : 128;
        for (var y = 0; y < 8; y++)
        {
            plane.AsSpan(offset + (y * stride), 8).Fill((byte)value);
        }
    }

    private static void Fill(byte[] plane, int offset, int stride, Func<int, int, Edges, int> sample, Edges edges)
    {
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                plane[offset + (y * stride) + x] = (byte)sample(x, y, edges);
            }
        }
    }

    private static void Require(bool available, string mode)
    {
        if (!available)
        {
            throw new InvalidDataException($"H.264: intra prediction mode {mode} needs neighbouring samples that are not available");
        }
    }

    private sealed class Edges
    {
        public Edges(ReadOnlySpan<int> top, ReadOnlySpan<int> left, int corner)
        {
            Top = top.ToArray();
            Left = left.ToArray();
            Corner = corner;
        }

        public int[] Top { get; }

        public int[] Left { get; }

        public int Corner { get; }

        public int T(int x) => x < 0 ? Corner : Top[x];

        public int L(int y) => y < 0 ? Corner : Left[y];
    }
}
