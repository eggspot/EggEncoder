namespace EggEncoder.Pcm;

internal static class FloatResampler
{
    private const int FilterHalfWidth = 32;
    private const int MaxHalfWidth = 256;
    private const int PhaseCount = 1024;
    private const double StopbandAttenuationDb = 80.0;

    public static float[] Resample(float[] samples, int channels, int sourceRate, int targetRate)
    {
        var sourceFrames = samples.Length / channels;
        if (sourceRate == targetRate || sourceFrames == 0)
        {
            return (float[])samples.Clone();
        }

        var targetFrames = (long)Math.Round(sourceFrames * (double)targetRate / sourceRate);
        if (targetFrames * channels > Array.MaxLength)
        {
            throw new ArgumentException("The resampled audio is too long to hold in one array", nameof(samples));
        }

        var cutoff = Math.Min(1.0, (double)targetRate / sourceRate);
        var halfWidth = cutoff < 1.0 ? (int)Math.Min(MaxHalfWidth, Math.Ceiling(FilterHalfWidth / cutoff)) : FilterHalfWidth;
        var table = WindowedSincKernel.BuildPolyphaseTable(halfWidth, cutoff, WindowedSincKernel.KaiserBeta(StopbandAttenuationDb), PhaseCount);
        var kernelLength = 2 * halfWidth;
        var output = new float[targetFrames * channels];

        for (var frame = 0L; frame < targetFrames; frame++)
        {
            var position = frame * (double)sourceRate / targetRate;
            var basePosition = (long)Math.Floor(position);
            var phase = Math.Min(PhaseCount - 1, (int)Math.Round((position - basePosition) * PhaseCount));
            var weights = table[phase];

            for (var channel = 0; channel < channels; channel++)
            {
                var sum = 0.0;
                for (var k = 0; k < kernelLength; k++)
                {
                    var source = Math.Clamp(basePosition + k - halfWidth + 1, 0, sourceFrames - 1);
                    sum += samples[(source * channels) + channel] * weights[k];
                }

                output[(frame * channels) + channel] = (float)sum;
            }
        }

        return output;
    }
}
