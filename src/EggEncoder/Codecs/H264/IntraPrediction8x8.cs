using System.Runtime.CompilerServices;

namespace EggEncoder.Codecs.H264;

[SkipLocalsInit]
internal static class IntraPrediction8x8
{
    public static void Predict(byte[] plane, int offset, int stride, int mode, bool left, bool top, bool topLeft, bool topRight)
    {
        Span<int> rawTop = stackalloc int[16];
        Span<int> rawLeft = stackalloc int[8];
        Span<int> edges = stackalloc int[25];
        var filteredLeft = edges[..8];
        var filteredTop = edges.Slice(9, 16);
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

        edges[8] = !topLeft ? 0
            : top && left ? (rawTop[0] + (2 * rawCorner) + rawLeft[0] + 2) >> 2
            : top ? ((3 * rawCorner) + rawTop[0] + 2) >> 2
            : left ? ((3 * rawCorner) + rawLeft[0] + 2) >> 2
            : rawCorner;

        switch (mode)
        {
            case 0:
                Require(top, "Intra_8x8_Vertical");
                for (var y = 0; y < 8; y++)
                {
                    for (var x = 0; x < 8; x++)
                    {
                        plane[offset + (y * stride) + x] = (byte)filteredTop[x];
                    }
                }

                break;
            case 1:
                Require(left, "Intra_8x8_Horizontal");
                for (var y = 0; y < 8; y++)
                {
                    plane.AsSpan(offset + (y * stride), 8).Fill((byte)filteredLeft[y]);
                }

                break;
            case 2:
                PredictDc(plane, offset, stride, top, left, filteredTop, filteredLeft);
                break;
            case 3:
                Require(top, "Intra_8x8_Diagonal_Down_Left");
                for (var y = 0; y < 8; y++)
                {
                    for (var x = 0; x < 8; x++)
                    {
                        plane[offset + (y * stride) + x] = (byte)(x == 7 && y == 7
                            ? (filteredTop[14] + (3 * filteredTop[15]) + 2) >> 2
                            : (filteredTop[x + y] + (2 * filteredTop[x + y + 1]) + filteredTop[x + y + 2] + 2) >> 2);
                    }
                }

                break;
            case 4:
                Require(top && left && topLeft, "Intra_8x8_Diagonal_Down_Right");
                for (var y = 0; y < 8; y++)
                {
                    for (var x = 0; x < 8; x++)
                    {
                        plane[offset + (y * stride) + x] = (byte)(x > y
                            ? (Top(edges, x - y - 2) + (2 * Top(edges, x - y - 1)) + Top(edges, x - y) + 2) >> 2
                            : x < y
                                ? (Left(edges, y - x - 2) + (2 * Left(edges, y - x - 1)) + Left(edges, y - x) + 2) >> 2
                                : (Top(edges, 0) + (2 * edges[8]) + Left(edges, 0) + 2) >> 2);
                    }
                }

                break;
            case 5:
                Require(top && left && topLeft, "Intra_8x8_Vertical_Right");
                for (var y = 0; y < 8; y++)
                {
                    for (var x = 0; x < 8; x++)
                    {
                        plane[offset + (y * stride) + x] = (byte)VerticalRight(edges, x, y);
                    }
                }

                break;
            case 6:
                Require(top && left && topLeft, "Intra_8x8_Horizontal_Down");
                for (var y = 0; y < 8; y++)
                {
                    for (var x = 0; x < 8; x++)
                    {
                        plane[offset + (y * stride) + x] = (byte)HorizontalDown(edges, x, y);
                    }
                }

                break;
            case 7:
                Require(top, "Intra_8x8_Vertical_Left");
                for (var y = 0; y < 8; y++)
                {
                    for (var x = 0; x < 8; x++)
                    {
                        var i = x + (y >> 1);
                        plane[offset + (y * stride) + x] = (byte)((y & 1) == 0
                            ? (filteredTop[i] + filteredTop[i + 1] + 1) >> 1
                            : (filteredTop[i] + (2 * filteredTop[i + 1]) + filteredTop[i + 2] + 2) >> 2);
                    }
                }

                break;
            case 8:
                Require(left, "Intra_8x8_Horizontal_Up");
                for (var y = 0; y < 8; y++)
                {
                    for (var x = 0; x < 8; x++)
                    {
                        plane[offset + (y * stride) + x] = (byte)HorizontalUp(filteredLeft, x, y);
                    }
                }

                break;
            default:
                throw new InvalidDataException("H.264: an Intra_8x8 prediction mode is out of range");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Top(ReadOnlySpan<int> edges, int x) => edges[9 + x];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Left(ReadOnlySpan<int> edges, int y) => y < 0 ? edges[8] : edges[y];

    private static int VerticalRight(ReadOnlySpan<int> edges, int x, int y)
    {
        var z = (2 * x) - y;
        var i = x - (y >> 1);
        return z switch
        {
            >= 0 when (z & 1) == 0 => (Top(edges, i - 1) + Top(edges, i) + 1) >> 1,
            > 0 => (Top(edges, i - 2) + (2 * Top(edges, i - 1)) + Top(edges, i) + 2) >> 2,
            -1 => (Left(edges, 0) + (2 * edges[8]) + Top(edges, 0) + 2) >> 2,
            _ => (Left(edges, y - (2 * x) - 1) + (2 * Left(edges, y - (2 * x) - 2)) + Left(edges, y - (2 * x) - 3) + 2) >> 2
        };
    }

    private static int HorizontalDown(ReadOnlySpan<int> edges, int x, int y)
    {
        var z = (2 * y) - x;
        var i = y - (x >> 1);
        return z switch
        {
            >= 0 when (z & 1) == 0 => (Left(edges, i - 1) + Left(edges, i) + 1) >> 1,
            > 0 => (Left(edges, i - 2) + (2 * Left(edges, i - 1)) + Left(edges, i) + 2) >> 2,
            -1 => (Left(edges, 0) + (2 * edges[8]) + Top(edges, 0) + 2) >> 2,
            _ => (Top(edges, x - (2 * y) - 1) + (2 * Top(edges, x - (2 * y) - 2)) + Top(edges, x - (2 * y) - 3) + 2) >> 2
        };
    }

    private static int HorizontalUp(ReadOnlySpan<int> left, int x, int y)
    {
        var z = x + (2 * y);
        var i = y + (x >> 1);
        return z switch
        {
            > 13 => left[7],
            13 => (left[6] + (3 * left[7]) + 2) >> 2,
            _ when (z & 1) == 0 => (left[i] + left[i + 1] + 1) >> 1,
            _ => (left[i] + (2 * left[i + 1]) + left[i + 2] + 2) >> 2
        };
    }

    private static void PredictDc(byte[] plane, int offset, int stride, bool top, bool left, ReadOnlySpan<int> filteredTop, ReadOnlySpan<int> filteredLeft)
    {
        var sumTop = 0;
        var sumLeft = 0;
        if (top)
        {
            for (var i = 0; i < 8; i++)
            {
                sumTop += filteredTop[i];
            }
        }

        if (left)
        {
            for (var i = 0; i < 8; i++)
            {
                sumLeft += filteredLeft[i];
            }
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

    private static void Require(bool available, string mode)
    {
        if (!available)
        {
            throw new InvalidDataException($"H.264: intra prediction mode {mode} needs neighbouring samples that are not available");
        }
    }
}
