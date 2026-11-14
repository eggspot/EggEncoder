namespace EggEncoder.Pcm;

internal static class WindowedSincKernel
{
    public static double[][] BuildPolyphaseTable(int halfWidth, double cutoff, double kaiserBeta, int phaseCount)
    {
        var kernelLength = 2 * halfWidth;
        var table = new double[phaseCount][];

        for (var phase = 0; phase < phaseCount; phase++)
        {
            var frac = (double)phase / phaseCount;
            var row = new double[kernelLength];
            var sum = 0.0;

            for (var k = 0; k < kernelLength; k++)
            {
                var offset = k - halfWidth + 1;
                var weight = Kernel(offset - frac, cutoff, halfWidth, kaiserBeta);
                row[k] = weight;
                sum += weight;
            }

            if (Math.Abs(sum) > 1e-12)
            {
                for (var k = 0; k < kernelLength; k++)
                {
                    row[k] /= sum;
                }
            }

            table[phase] = row;
        }

        return table;
    }

    public static double KaiserBeta(double stopbandAttenuationDb)
    {
        if (stopbandAttenuationDb > 50.0)
        {
            return 0.1102 * (stopbandAttenuationDb - 8.7);
        }

        if (stopbandAttenuationDb >= 21.0)
        {
            return (0.5842 * Math.Pow(stopbandAttenuationDb - 21.0, 0.4)) + (0.07886 * (stopbandAttenuationDb - 21.0));
        }

        return 0.0;
    }

    private static double Kernel(double x, double cutoff, int halfWidth, double kaiserBeta)
    {
        var window = KaiserWindow(x / halfWidth, kaiserBeta);
        if (window == 0.0)
        {
            return 0.0;
        }

        if (Math.Abs(x) < 1e-9)
        {
            return cutoff * window;
        }

        var piX = Math.PI * x;
        return Math.Sin(piX * cutoff) / piX * window;
    }

    private static double KaiserWindow(double xNormalized, double beta)
    {
        if (xNormalized <= -1.0 || xNormalized >= 1.0)
        {
            return 0.0;
        }

        return BesselI0(beta * Math.Sqrt(1.0 - (xNormalized * xNormalized))) / BesselI0(beta);
    }

    private static double BesselI0(double x)
    {
        var sum = 1.0;
        var term = 1.0;
        var quarterXSquared = x * x / 4.0;

        for (var k = 1; k <= 50; k++)
        {
            term *= quarterXSquared / (k * k);
            sum += term;
            if (term < sum * 1e-16)
            {
                break;
            }
        }

        return sum;
    }
}
