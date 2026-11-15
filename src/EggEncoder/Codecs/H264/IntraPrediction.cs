namespace EggEncoder.Codecs.H264;

internal static class IntraPrediction
{
    public static void Predict4x4(byte[] plane, int offset, int stride, int mode, bool left, bool top, bool topLeft, bool topRight)
    {
        Span<int> above = stackalloc int[8];
        Span<int> side = stackalloc int[4];
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

        var corner = topLeft ? plane[offset - stride - 1] : 0;
        switch (mode)
        {
            case 0:
                Require(top, "vertical");
                for (var y = 0; y < 4; y++)
                {
                    for (var x = 0; x < 4; x++)
                    {
                        plane[offset + (y * stride) + x] = (byte)above[x];
                    }
                }

                break;
            case 1:
                Require(left, "horizontal");
                for (var y = 0; y < 4; y++)
                {
                    for (var x = 0; x < 4; x++)
                    {
                        plane[offset + (y * stride) + x] = (byte)side[y];
                    }
                }

                break;
            case 2:
                int dc;
                if (top && left)
                {
                    dc = (above[0] + above[1] + above[2] + above[3] + side[0] + side[1] + side[2] + side[3] + 4) >> 3;
                }
                else if (left)
                {
                    dc = (side[0] + side[1] + side[2] + side[3] + 2) >> 2;
                }
                else if (top)
                {
                    dc = (above[0] + above[1] + above[2] + above[3] + 2) >> 2;
                }
                else
                {
                    dc = 128;
                }

                Fill(plane, offset, stride, 4, 4, dc);
                break;
            case 3:
                Require(top, "diagonal down left");
                for (var y = 0; y < 4; y++)
                {
                    for (var x = 0; x < 4; x++)
                    {
                        plane[offset + (y * stride) + x] = (byte)(x == 3 && y == 3
                            ? (above[6] + (3 * above[7]) + 2) >> 2
                            : (above[x + y] + (2 * above[x + y + 1]) + above[x + y + 2] + 2) >> 2);
                    }
                }

                break;
            case 4:
                Require(top && left && topLeft, "diagonal down right");
                for (var y = 0; y < 4; y++)
                {
                    for (var x = 0; x < 4; x++)
                    {
                        int value;
                        if (x > y)
                        {
                            value = (Above(above, corner, x - y - 2) + (2 * Above(above, corner, x - y - 1)) + above[x - y] + 2) >> 2;
                        }
                        else if (x < y)
                        {
                            value = (Left(side, corner, y - x - 2) + (2 * Left(side, corner, y - x - 1)) + side[y - x] + 2) >> 2;
                        }
                        else
                        {
                            value = (above[0] + (2 * corner) + side[0] + 2) >> 2;
                        }

                        plane[offset + (y * stride) + x] = (byte)value;
                    }
                }

                break;
            case 5:
                Require(top && left && topLeft, "vertical right");
                for (var y = 0; y < 4; y++)
                {
                    for (var x = 0; x < 4; x++)
                    {
                        var zVR = (2 * x) - y;
                        int value;
                        if (zVR >= 0 && (zVR & 1) == 0)
                        {
                            value = (Above(above, corner, x - (y >> 1) - 1) + Above(above, corner, x - (y >> 1)) + 1) >> 1;
                        }
                        else if (zVR >= 0)
                        {
                            value = (Above(above, corner, x - (y >> 1) - 2) + (2 * Above(above, corner, x - (y >> 1) - 1)) + Above(above, corner, x - (y >> 1)) + 2) >> 2;
                        }
                        else if (zVR == -1)
                        {
                            value = (side[0] + (2 * corner) + above[0] + 2) >> 2;
                        }
                        else
                        {
                            value = (Left(side, corner, y - 1) + (2 * Left(side, corner, y - 2)) + Left(side, corner, y - 3) + 2) >> 2;
                        }

                        plane[offset + (y * stride) + x] = (byte)value;
                    }
                }

                break;
            case 6:
                Require(top && left && topLeft, "horizontal down");
                for (var y = 0; y < 4; y++)
                {
                    for (var x = 0; x < 4; x++)
                    {
                        var zHD = (2 * y) - x;
                        int value;
                        if (zHD >= 0 && (zHD & 1) == 0)
                        {
                            value = (Left(side, corner, y - (x >> 1) - 1) + Left(side, corner, y - (x >> 1)) + 1) >> 1;
                        }
                        else if (zHD >= 0)
                        {
                            value = (Left(side, corner, y - (x >> 1) - 2) + (2 * Left(side, corner, y - (x >> 1) - 1)) + Left(side, corner, y - (x >> 1)) + 2) >> 2;
                        }
                        else if (zHD == -1)
                        {
                            value = (side[0] + (2 * corner) + above[0] + 2) >> 2;
                        }
                        else
                        {
                            value = (Above(above, corner, x - 1) + (2 * Above(above, corner, x - 2)) + Above(above, corner, x - 3) + 2) >> 2;
                        }

                        plane[offset + (y * stride) + x] = (byte)value;
                    }
                }

                break;
            case 7:
                Require(top, "vertical left");
                for (var y = 0; y < 4; y++)
                {
                    for (var x = 0; x < 4; x++)
                    {
                        var i = x + (y >> 1);
                        plane[offset + (y * stride) + x] = (byte)((y & 1) == 0
                            ? (above[i] + above[i + 1] + 1) >> 1
                            : (above[i] + (2 * above[i + 1]) + above[i + 2] + 2) >> 2);
                    }
                }

                break;
            case 8:
                Require(left, "horizontal up");
                for (var y = 0; y < 4; y++)
                {
                    for (var x = 0; x < 4; x++)
                    {
                        var zHU = x + (2 * y);
                        int value;
                        if (zHU > 5)
                        {
                            value = side[3];
                        }
                        else if (zHU == 5)
                        {
                            value = (side[2] + (3 * side[3]) + 2) >> 2;
                        }
                        else if ((zHU & 1) == 0)
                        {
                            value = (side[y + (x >> 1)] + side[y + (x >> 1) + 1] + 1) >> 1;
                        }
                        else
                        {
                            value = (side[y + (x >> 1)] + (2 * side[y + (x >> 1) + 1]) + side[y + (x >> 1) + 2] + 2) >> 2;
                        }

                        plane[offset + (y * stride) + x] = (byte)value;
                    }
                }

                break;
            default:
                throw new InvalidDataException($"H.264: intra 4x4 prediction mode {mode} is invalid");
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

    private static int Above(Span<int> above, int corner, int index) => index < 0 ? corner : above[index];

    private static int Left(Span<int> side, int corner, int index) => index < 0 ? corner : side[index];

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
