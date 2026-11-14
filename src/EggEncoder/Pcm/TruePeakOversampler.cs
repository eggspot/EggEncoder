using System.Numerics;

namespace EggEncoder.Pcm;

internal static class TruePeakOversampler
{
    public const int Reach = HalfWidth;

    private const int HalfWidth = 24;
    private const int Taps = 2 * HalfWidth;
    private const int Phases = 4;
    private const double StopbandAttenuationDb = 80.0;
    private const double GainMargin = 1e-4;

    private static readonly float[][] Rows = WindowedSincKernel
        .BuildPolyphaseTable(HalfWidth, 1.0, WindowedSincKernel.KaiserBeta(StopbandAttenuationDb), Phases)
        .Skip(1)
        .Select(row => row.Select(weight => (float)weight).ToArray())
        .ToArray();

    private static readonly double Gain = Rows.Max(row => row.Sum(weight => Math.Abs((double)weight))) * (1.0 + GainMargin);

    public static double Peak(float[] samples, int channels)
    {
        var frames = samples.Length / channels;
        var padded = Deinterleave(samples, channels, frames);
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
                peak = Math.Max(peak, FramePeak(padded, frame));
            }
        }

        return peak;
    }

    public static float FramePeaksAbove(float[] samples, int channels, double threshold, float[] peaks)
    {
        var frames = samples.Length / channels;
        var padded = Deinterleave(samples, channels, frames);
        var framePeaks = SamplePeaks(samples, channels);
        SlidingWindow.Max(framePeaks, frames, HalfWidth - 1, HalfWidth, peaks);
        var largest = 0f;
        for (var frame = 0; frame < frames; frame++)
        {
            peaks[frame] = peaks[frame] * Gain > threshold ? FramePeak(padded, frame) : 0f;
            largest = Math.Max(largest, peaks[frame]);
        }

        return largest;
    }

    private static float[][] Deinterleave(float[] samples, int channels, int frames)
    {
        var padded = new float[channels][];
        for (var channel = 0; channel < channels; channel++)
        {
            var values = new float[frames + Taps];
            for (var frame = 0; frame < frames; frame++)
            {
                values[frame + HalfWidth] = samples[(frame * channels) + channel];
            }

            padded[channel] = values;
        }

        return padded;
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

    private static float FramePeak(float[][] padded, int frame)
    {
        var peak = 0f;
        foreach (var values in padded)
        {
            peak = Math.Max(peak, Math.Abs(values[frame + HalfWidth]));
            var window = values.AsSpan(frame + 1, Taps);
            foreach (var row in Rows)
            {
                peak = Math.Max(peak, Math.Abs(Dot(window, row)));
            }
        }

        return peak;
    }

    private static float Dot(ReadOnlySpan<float> values, float[] weights)
    {
        var sum = Vector<float>.Zero;
        var i = 0;
        for (; i <= values.Length - Vector<float>.Count; i += Vector<float>.Count)
        {
            sum += new Vector<float>(values[i..]) * new Vector<float>(weights, i);
        }

        var total = Vector.Dot(sum, Vector<float>.One);
        for (; i < values.Length; i++)
        {
            total += values[i] * weights[i];
        }

        return total;
    }
}
