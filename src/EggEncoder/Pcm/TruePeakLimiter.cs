namespace EggEncoder.Pcm;

internal static class TruePeakLimiter
{
    private const int MaxPasses = 4;
    private const double LookaheadSeconds = 0.005;
    private const double ReleaseSeconds = 0.1;
    private const double FallbackMargin = 1e-6;

    public static void Apply(float[] samples, int channels, int sampleRate, double ceiling)
    {
        var frames = samples.Length / channels;
        if (frames == 0)
        {
            return;
        }

        var lookahead = Math.Max(1, (int)Math.Round(LookaheadSeconds * sampleRate));
        var release = 1.0 - Math.Exp(-1.0 / (ReleaseSeconds * sampleRate));
        var first = new float[frames];
        var second = new float[frames];

        for (var pass = 0; pass < MaxPasses; pass++)
        {
            if (TruePeakOversampler.FramePeaksAbove(samples, channels, ceiling, first) <= ceiling)
            {
                return;
            }

            for (var frame = 0; frame < frames; frame++)
            {
                first[frame] = first[frame] > ceiling ? (float)(ceiling / first[frame]) : 1f;
            }

            SlidingWindow.Min(first, frames, TruePeakOversampler.Reach, TruePeakOversampler.Reach, second);
            SlidingWindow.Min(second, frames, 0, lookahead - 1, first);
            Smooth(first, second, frames, lookahead, release);

            for (var frame = 0; frame < frames; frame++)
            {
                for (var channel = 0; channel < channels; channel++)
                {
                    samples[(frame * channels) + channel] *= second[frame];
                }
            }
        }

        double peak;
        while ((peak = TruePeakOversampler.Peak(samples, channels)) > ceiling)
        {
            var gain = (float)(ceiling / peak * (1.0 - FallbackMargin));
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] *= gain;
            }
        }
    }

    private static void Smooth(float[] lookaheadMin, float[] gains, int frames, int lookahead, double release)
    {
        var sum = lookahead * (double)lookaheadMin[0];
        var previous = 1.0;
        for (var frame = 0; frame < frames; frame++)
        {
            sum += lookaheadMin[frame] - (frame >= lookahead ? lookaheadMin[frame - lookahead] : lookaheadMin[0]);
            var gain = Math.Min(sum / lookahead, previous + ((1.0 - previous) * release));
            gains[frame] = (float)gain;
            previous = gain;
        }
    }
}
