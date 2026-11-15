namespace EggEncoder.Codecs.H264;

internal static class IntraPrediction
{
    public static void Predict4x4(byte[] plane, int offset, int stride, int mode, bool left, bool top, bool topLeft, bool topRight)
    {
        Span<int> above = stackalloc int[8];
        Span<int> side = stackalloc int[4];
        var corner = Gather4x4Neighbours(plane, offset, stride, left, top, topLeft, topRight, above, side);
        Predict4x4(above, side, corner, mode, left, top, topLeft, plane.AsSpan(offset), stride);
    }

    public static int Gather4x4Neighbours(byte[] plane, int offset, int stride, bool left, bool top, bool topLeft, bool topRight, Span<int> above, Span<int> side)
    {
        if (top)
        {
            for (var x = 0; x < 4; x++)
            {
                above[x] = plane[offset - stride + x];
            }

            for (var x = 4; x < 8; x++)
            {
                above[x] = topRight ? plane[offset - stride + x] : above[3];
            }
        }

        if (left)
        {
            for (var y = 0; y < 4; y++)
            {
                side[y] = plane[offset + (y * stride) - 1];
            }
        }

        return topLeft ? plane[offset - stride - 1] : 0;
    }

    public const int EdgeValueCount = 41;

    private const int ThreeTap = 0;
    private const int TwoTap = 14;
    private const int Raw = 27;

    private static readonly byte[][] ModeTables = BuildModeTables();

    public static void Predict4x4(Span<int> above, Span<int> side, int corner, int mode, bool left, bool top, bool topLeft, Span<byte> output, int outputStride)
    {
        Span<int> values = stackalloc int[EdgeValueCount];
        Edge4x4Values(above, side, corner, values);
        Predict4x4(values, mode, left, top, topLeft, output, outputStride);
    }

    public static void Edge4x4Values(ReadOnlySpan<int> above, ReadOnlySpan<int> side, int corner, Span<int> values)
    {
        Span<int> edge = stackalloc int[14];
        edge[0] = side[3];
        edge[1] = side[2];
        edge[2] = side[1];
        edge[3] = side[0];
        edge[4] = corner;
        for (var x = 0; x < 8; x++)
        {
            edge[5 + x] = above[x];
        }

        edge[13] = above[7];
        for (var k = 0; k < 14; k++)
        {
            values[ThreeTap + k] = (edge[Math.Max(k - 1, 0)] + (2 * edge[k]) + edge[Math.Min(k + 1, 13)] + 2) >> 2;
            values[Raw + k] = edge[k];
        }

        for (var k = 0; k < 13; k++)
        {
            values[TwoTap + k] = (edge[k] + edge[k + 1] + 1) >> 1;
        }
    }

    public static void Predict4x4(ReadOnlySpan<int> values, int mode, bool left, bool top, bool topLeft, Span<byte> output, int outputStride)
    {
        switch (mode)
        {
            case 0:
                Require(top, "vertical");
                break;
            case 1:
                Require(left, "horizontal");
                break;
            case 2:
                int dc;
                var aboveSum = values[Raw + 5] + values[Raw + 6] + values[Raw + 7] + values[Raw + 8];
                var leftSum = values[Raw] + values[Raw + 1] + values[Raw + 2] + values[Raw + 3];
                if (top && left)
                {
                    dc = (aboveSum + leftSum + 4) >> 3;
                }
                else if (left)
                {
                    dc = (leftSum + 2) >> 2;
                }
                else if (top)
                {
                    dc = (aboveSum + 2) >> 2;
                }
                else
                {
                    dc = 128;
                }

                for (var y = 0; y < 4; y++)
                {
                    output.Slice(y * outputStride, 4).Fill((byte)dc);
                }

                return;
            case 3:
                Require(top, "diagonal down left");
                break;
            case 4:
                Require(top && left && topLeft, "diagonal down right");
                break;
            case 5:
                Require(top && left && topLeft, "vertical right");
                break;
            case 6:
                Require(top && left && topLeft, "horizontal down");
                break;
            case 7:
                Require(top, "vertical left");
                break;
            case 8:
                Require(left, "horizontal up");
                break;
            default:
                throw new InvalidDataException($"H.264: intra 4x4 prediction mode {mode} is invalid");
        }

        var table = ModeTables[mode];
        for (var y = 0; y < 4; y++)
        {
            var row = output.Slice(y * outputStride, 4);
            row[0] = (byte)values[table[y * 4]];
            row[1] = (byte)values[table[(y * 4) + 1]];
            row[2] = (byte)values[table[(y * 4) + 2]];
            row[3] = (byte)values[table[(y * 4) + 3]];
        }
    }

    private static byte[][] BuildModeTables()
    {
        var tables = new byte[9][];
        for (var mode = 0; mode < 9; mode++)
        {
            var table = new byte[16];
            for (var y = 0; y < 4; y++)
            {
                for (var x = 0; x < 4; x++)
                {
                    table[(y * 4) + x] = (byte)ModeValueIndex(mode, x, y);
                }
            }

            tables[mode] = table;
        }

        return tables;
    }

    private static int ModeValueIndex(int mode, int x, int y)
    {
        switch (mode)
        {
            case 0:
                return Raw + 5 + x;
            case 1:
                return Raw + 3 - y;
            case 3:
                return ThreeTap + 6 + x + y;
            case 4:
                return ThreeTap + 4 + x - y;
            case 5:
                var zVr = (2 * x) - y;
                return zVr >= 0 ? ((zVr & 1) == 0 ? TwoTap : ThreeTap) + 4 + x - (y >> 1)
                    : zVr == -1 ? ThreeTap + 4
                    : ThreeTap + 5 - y;
            case 6:
                var zHd = (2 * y) - x;
                return zHd >= 0 ? ((zHd & 1) == 0 ? TwoTap + 3 - y + (x >> 1) : ThreeTap + 4 - y + (x >> 1))
                    : zHd == -1 ? ThreeTap + 4
                    : ThreeTap + 3 + x;
            case 7:
                var i = x + (y >> 1);
                return (y & 1) == 0 ? TwoTap + 5 + i : ThreeTap + 6 + i;
            case 8:
                var zHu = x + (2 * y);
                var j = y + (x >> 1);
                return zHu > 5 ? Raw
                    : zHu == 5 ? ThreeTap
                    : (zHu & 1) == 0 ? TwoTap + 2 - j
                    : ThreeTap + 2 - j;
            default:
                return 0;
        }
    }

    public static void Predict16x16(byte[] plane, int offset, int stride, int mode, bool left, bool top, bool topLeft)
    {
        switch (mode)
        {
            case 0:
                Require(top, "16x16 vertical");
                for (var y = 0; y < 16; y++)
                {
                    Buffer.BlockCopy(plane, offset - stride, plane, offset + (y * stride), 16);
                }

                break;
            case 1:
                Require(left, "16x16 horizontal");
                for (var y = 0; y < 16; y++)
                {
                    plane.AsSpan(offset + (y * stride), 16).Fill(plane[offset + (y * stride) - 1]);
                }

                break;
            case 2:
                var sum = 0;
                if (top)
                {
                    for (var x = 0; x < 16; x++)
                    {
                        sum += plane[offset - stride + x];
                    }
                }

                if (left)
                {
                    for (var y = 0; y < 16; y++)
                    {
                        sum += plane[offset + (y * stride) - 1];
                    }
                }

                var dc = top && left ? (sum + 16) >> 5 : top || left ? (sum + 8) >> 4 : 128;
                Fill(plane, offset, stride, 16, 16, dc);
                break;
            case 3:
                Require(top && left && topLeft, "16x16 plane");
                Plane(plane, offset, stride, 16, 16, 5, 5);
                break;
            default:
                throw new InvalidDataException($"H.264: intra 16x16 prediction mode {mode} is invalid");
        }
    }

    public static void PredictChroma(byte[] plane, int offset, int stride, int mode, bool left, bool top, bool topLeft)
    {
        switch (mode)
        {
            case 0:
                for (var blockY = 0; blockY < 2; blockY++)
                {
                    for (var blockX = 0; blockX < 2; blockX++)
                    {
                        var (useTop, useLeft) = (blockX, blockY) switch
                        {
                            (1, 0) => (top, !top && left),
                            (0, 1) => (!left && top, left),
                            _ => (top, left)
                        };
                        var total = 0;
                        if (useTop)
                        {
                            for (var x = 0; x < 4; x++)
                            {
                                total += plane[offset - stride + (4 * blockX) + x];
                            }
                        }

                        if (useLeft)
                        {
                            for (var y = 0; y < 4; y++)
                            {
                                total += plane[offset + (((4 * blockY) + y) * stride) - 1];
                            }
                        }

                        var value = useTop && useLeft ? (total + 4) >> 3 : useTop || useLeft ? (total + 2) >> 2 : 128;
                        Fill(plane, offset + (4 * blockY * stride) + (4 * blockX), stride, 4, 4, value);
                    }
                }

                break;
            case 1:
                Require(left, "chroma horizontal");
                for (var y = 0; y < 8; y++)
                {
                    plane.AsSpan(offset + (y * stride), 8).Fill(plane[offset + (y * stride) - 1]);
                }

                break;
            case 2:
                Require(top, "chroma vertical");
                for (var y = 0; y < 8; y++)
                {
                    Buffer.BlockCopy(plane, offset - stride, plane, offset + (y * stride), 8);
                }

                break;
            case 3:
                Require(top && left && topLeft, "chroma plane");
                Plane(plane, offset, stride, 8, 8, 34, 34);
                break;
            default:
                throw new InvalidDataException($"H.264: intra chroma prediction mode {mode} is invalid");
        }
    }

    private static void Plane(byte[] plane, int offset, int stride, int width, int height, int horizontalScale, int verticalScale)
    {
        var halfWidth = width / 2;
        var halfHeight = height / 2;
        var h = 0;
        for (var x = 0; x < halfWidth; x++)
        {
            h += (x + 1) * (plane[offset - stride + halfWidth + x] - plane[offset - stride + halfWidth - 2 - x]);
        }

        var v = 0;
        for (var y = 0; y < halfHeight; y++)
        {
            v += (y + 1) * (plane[offset + ((halfHeight + y) * stride) - 1] - plane[offset + ((halfHeight - 2 - y) * stride) - 1]);
        }

        var a = 16 * (plane[offset + ((height - 1) * stride) - 1] + plane[offset - stride + width - 1]);
        var b = ((horizontalScale * h) + 32) >> 6;
        var c = ((verticalScale * v) + 32) >> 6;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                plane[offset + (y * stride) + x] = Clip((a + (b * (x - (halfWidth - 1))) + (c * (y - (halfHeight - 1))) + 16) >> 5);
            }
        }
    }

    private static void Fill(byte[] plane, int offset, int stride, int width, int height, int value)
    {
        for (var y = 0; y < height; y++)
        {
            plane.AsSpan(offset + (y * stride), width).Fill((byte)value);
        }
    }

    private static void Require(bool available, string mode)
    {
        if (!available)
        {
            throw new InvalidDataException($"H.264: intra prediction mode {mode} needs neighbouring samples that are not available");
        }
    }

    public static byte Clip(int value) => (byte)Math.Clamp(value, 0, 255);
}
