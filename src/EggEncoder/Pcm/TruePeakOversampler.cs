namespace EggEncoder.Pcm;

internal static class TruePeakOversampler
{
    public const int Reach = HalfWidth;

    private const int HalfWidth = 24;
    private const int Phases = 4;
    private const double StopbandAttenuationDb = 80.0;

    private static readonly double[][] Table = WindowedSincKernel.BuildPolyphaseTable(HalfWidth, 1.0, WindowedSincKernel.KaiserBeta(StopbandAttenuationDb), Phases);
    private static readonly double Gain = Table.Skip(1).Max(row => row.Sum(Math.Abs));

    public static double Peak(float[] samples, int channels)
    {
        var frames = samples.Length / channels;
        var framePeaks = SamplePeaks(samples, channels);
        var reachMax = new float[frames];
        SlidingWindow.Max(framePeaks, frames, HalfWidth - 1, HalfWidth, reachMax);

        var peak = 0.0;
        foreach (var value in framePeaks)
        {
            peak = Math.Max(peak, value);
        }

        for (var frame = 0; frame < frames; frame++)
        {
            if (reachMax[frame] * Gain > peak)
            {
                peak = Math.Max(peak, FramePeak(samples, channels, frames, frame));
            }
        }

        return peak;
    }

    public static void FramePeaksAbove(float[] samples, int channels, double threshold, float[] peaks)
    {
        var frames = samples.Length / channels;
        var framePeaks = SamplePeaks(samples, channels);
        SlidingWindow.Max(framePeaks, frames, HalfWidth - 1, HalfWidth, peaks);
        for (var frame = 0; frame < frames; frame++)
        {
            peaks[frame] = peaks[frame] * Gain > threshold ? (float)FramePeak(samples, channels, frames, frame) : 0f;
        }
    }

    private static float[] SamplePeaks(float[] samples, int channels)
    {
        var peaks = new float[samples.Length / channels];
        for (var frame = 0; frame < peaks.Length; frame++)
        {
            var peak = 0f;
            for (var channel = 0; channel < channels; channel++)
            {
                peak = Math.Max(peak, Math.Abs(samples[(frame * channels) + channel]));
            }

            peaks[frame] = peak;
        }

        return peaks;
    }

    private static double FramePeak(float[] samples, int channels, int frames, int frame)
    {
        var peak = 0.0;
        for (var channel = 0; channel < channels; channel++)
        {
            peak = Math.Max(peak, Math.Abs(samples[(frame * channels) + channel]));
            for (var phase = 1; phase < Phases; phase++)
            {
                var row = Table[phase];
                var sum = 0.0;
                for (var k = 0; k < row.Length; k++)
                {
                    var source = frame + k - HalfWidth + 1;
                    if (source >= 0 && source < frames)
                    {
                        sum += samples[(source * channels) + channel] * row[k];
                    }
                }

                peak = Math.Max(peak, Math.Abs(sum));
            }
        }

        return peak;
    }
}
