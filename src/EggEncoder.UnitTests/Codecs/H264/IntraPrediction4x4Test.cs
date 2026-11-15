using EggEncoder.Codecs.H264;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.H264
{
    public class IntraPrediction4x4Test
    {
        [Fact]
        public void Every_Mode_Should_Equal_The_Per_Pixel_Formulas_Of_The_Standard()
        {
            var random = new Random(23);
            for (var round = 0; round < 2000; round++)
            {
                var above = new int[8];
                var side = new int[4];
                for (var i = 0; i < 8; i++)
                {
                    above[i] = round % 50 == 0 ? 255 : random.Next(256);
                }

                for (var i = 0; i < 4; i++)
                {
                    side[i] = round % 50 == 0 ? 255 : random.Next(256);
                }

                var corner = random.Next(256);
                var left = random.Next(4) != 0;
                var top = random.Next(4) != 0;
                var topLeft = left && top && random.Next(4) != 0;
                for (var mode = 0; mode < 9; mode++)
                {
                    var expected = new byte[16];
                    var actual = new byte[16];
                    var expectedError = Run(() => ReferencePredict4x4(above, side, corner, mode, left, top, topLeft, expected, 4));
                    var actualError = Run(() => IntraPrediction.Predict4x4(above, side, corner, mode, left, top, topLeft, actual, 4));

                    (actualError is null).Should().Be(expectedError is null);
                    if (expectedError is null)
                    {
                        actual.Should().Equal(expected, $"mode {mode}");
                    }
                }
            }
        }

        private static Exception? Run(Action action)
        {
            try
            {
                action();
                return null;
            }
            catch (InvalidDataException error)
            {
                return error;
            }
        }

        private static void ReferencePredict4x4(Span<int> above, Span<int> side, int corner, int mode, bool left, bool top, bool topLeft, Span<byte> output, int outputStride)
        {
            switch (mode)
            {
                case 0:
                    RequireAvailable(top, "vertical");
                    for (var y = 0; y < 4; y++)
                    {
                        for (var x = 0; x < 4; x++)
                        {
                            output[(y * outputStride) + x] = (byte)above[x];
                        }
                    }

                    break;
                case 1:
                    RequireAvailable(left, "horizontal");
                    for (var y = 0; y < 4; y++)
                    {
                        for (var x = 0; x < 4; x++)
                        {
                            output[(y * outputStride) + x] = (byte)side[y];
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

                    for (var y = 0; y < 4; y++)
                    {
                        output.Slice(y * outputStride, 4).Fill((byte)dc);
                    }
                    break;
                case 3:
                    RequireAvailable(top, "diagonal down left");
                    for (var y = 0; y < 4; y++)
                    {
                        for (var x = 0; x < 4; x++)
                        {
                            output[(y * outputStride) + x] = (byte)(x == 3 && y == 3
                                ? (above[6] + (3 * above[7]) + 2) >> 2
                                : (above[x + y] + (2 * above[x + y + 1]) + above[x + y + 2] + 2) >> 2);
                        }
                    }

                    break;
                case 4:
                    RequireAvailable(top && left && topLeft, "diagonal down right");
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

                            output[(y * outputStride) + x] = (byte)value;
                        }
                    }

                    break;
                case 5:
                    RequireAvailable(top && left && topLeft, "vertical right");
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

                            output[(y * outputStride) + x] = (byte)value;
                        }
                    }

                    break;
                case 6:
                    RequireAvailable(top && left && topLeft, "horizontal down");
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

                            output[(y * outputStride) + x] = (byte)value;
                        }
                    }

                    break;
                case 7:
                    RequireAvailable(top, "vertical left");
                    for (var y = 0; y < 4; y++)
                    {
                        for (var x = 0; x < 4; x++)
                        {
                            var i = x + (y >> 1);
                            output[(y * outputStride) + x] = (byte)((y & 1) == 0
                                ? (above[i] + above[i + 1] + 1) >> 1
                                : (above[i] + (2 * above[i + 1]) + above[i + 2] + 2) >> 2);
                        }
                    }

                    break;
                case 8:
                    RequireAvailable(left, "horizontal up");
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

                            output[(y * outputStride) + x] = (byte)value;
                        }
                    }

                    break;
                default:
                    throw new InvalidDataException($"H.264: intra 4x4 prediction mode {mode} is invalid");
            }
        }

        private static int Above(Span<int> above, int corner, int index) => index < 0 ? corner : above[index];

        private static int Left(Span<int> side, int corner, int index) => index < 0 ? corner : side[index];

        private static void RequireAvailable(bool available, string mode)
        {
            if (!available)
            {
                throw new InvalidDataException(mode);
            }
        }
    }
}
