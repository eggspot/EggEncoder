namespace EggEncoder.Codecs.Mpeg2
{
    internal static class Mpeg2Idct
    {
        private static readonly double[] Basis = BuildBasis();

        public static void Inverse(Span<int> coefficients, Span<int> output)
        {
            Span<double> temporary = stackalloc double[64];
            for (var y = 0; y < 8; y++)
            {
                for (var x = 0; x < 8; x++)
                {
                    double sum = 0;
                    for (var u = 0; u < 8; u++)
                    {
                        var coefficient = coefficients[(y * 8) + u];
                        if (coefficient != 0)
                        {
                            sum += coefficient * Basis[(x * 8) + u];
                        }
                    }

                    temporary[(y * 8) + x] = sum;
                }
            }

            for (var x = 0; x < 8; x++)
            {
                for (var y = 0; y < 8; y++)
                {
                    double sum = 0;
                    for (var v = 0; v < 8; v++)
                    {
                        sum += temporary[(v * 8) + x] * Basis[(y * 8) + v];
                    }

                    output[(y * 8) + x] = Math.Clamp((int)Math.Round(sum, MidpointRounding.AwayFromZero), -256, 255);
                }
            }
        }

        private static double[] BuildBasis()
        {
            var basis = new double[64];
            for (var x = 0; x < 8; x++)
            {
                for (var u = 0; u < 8; u++)
                {
                    var scale = u == 0 ? Math.Sqrt(0.125) : 0.5;
                    basis[(x * 8) + u] = scale * Math.Cos(((2 * x) + 1) * u * Math.PI / 16);
                }
            }

            return basis;
        }
    }
}
