namespace EggEncoder.Pcm;

/// <summary>
/// Applies fade-in and/or fade-out over a fixed total number of output frames, spread across
/// however many blocks the pipeline delivers them in.
///
/// The transform is constructed with the exact frame count of the range it will see (e.g. the
/// retained span of a Cut) and tracks its position across successive <see cref="Apply"/> calls,
/// the same way <see cref="ResamplingTransform"/> tracks fractional source position across blocks.
/// Without this, a fade that spans more than one decode block (anything longer than a few thousand
/// samples) would restart its ramp at the start of every block instead of running once over the
/// whole range.
/// </summary>
public sealed class FadeTransform : IPcmTransform
{
    private readonly long _totalFrames;
    private readonly long _fadeInFrames;
    private readonly long _fadeOutFrames;
    private readonly FadeCurve _curve;
    private long _framePosition;

    /// <param name="totalFrames">Total number of frames this transform will process across all Apply calls.</param>
    /// <param name="fadeInFrames">Number of frames over which to fade in (0 = no fade-in).</param>
    /// <param name="fadeOutFrames">Number of frames over which to fade out (0 = no fade-out).</param>
    /// <param name="curve">Fade curve shape (linear or equal-power cosine).</param>
    public FadeTransform(
        long totalFrames,
        long fadeInFrames = 0,
        long fadeOutFrames = 0,
        FadeCurve curve = FadeCurve.Linear)
    {
        if (totalFrames < 0) throw new ArgumentOutOfRangeException(nameof(totalFrames), totalFrames, "Total frame count must be non-negative");
        if (fadeInFrames < 0) throw new ArgumentOutOfRangeException(nameof(fadeInFrames), fadeInFrames, "Fade frame counts must be non-negative");
        if (fadeOutFrames < 0) throw new ArgumentOutOfRangeException(nameof(fadeOutFrames), fadeOutFrames, "Fade frame counts must be non-negative");

        _totalFrames = totalFrames;
        _fadeInFrames = Math.Min(fadeInFrames, totalFrames);
        _fadeOutFrames = Math.Min(fadeOutFrames, totalFrames);
        _curve = curve;
    }

    public int OutputSampleRate => 0;   // passthrough — preserves input rate
    public int OutputChannels => 0;     // passthrough — preserves input channels
    public int OutputBitsPerSample => 0;
    public bool CanChangeFrameCount => false;

    /// <summary>Reset the cross-block frame position tracker. Call at the start of a new stream.</summary>
    public void Reset()
    {
        _framePosition = 0;
    }

    public (int[] buffer, int frameCount) Apply(int[] buffer, int frameCount, int channels, int sampleRate, int bitsPerSample)
    {
        if (_fadeInFrames == 0 && _fadeOutFrames == 0)
        {
            _framePosition += frameCount;
            return (buffer, frameCount);
        }

        var fadeOutStart = _totalFrames - _fadeOutFrames;

        for (var frame = 0; frame < frameCount; frame++)
        {
            var globalFrame = _framePosition + frame;
            var gain = 1.0;

            if (globalFrame < _fadeInFrames)
            {
                gain = Math.Min(gain, ComputeFadeGain(globalFrame, _fadeInFrames, _curve));
            }

            if (globalFrame >= fadeOutStart)
            {
                var framesFromEnd = _totalFrames - 1 - globalFrame;
                gain = Math.Min(gain, ComputeFadeGain(framesFromEnd, _fadeOutFrames, _curve));
            }

            if (gain != 1.0)
            {
                var frameStart = frame * channels;
                for (var ch = 0; ch < channels; ch++)
                {
                    var idx = frameStart + ch;
                    buffer[idx] = (int)Math.Round(buffer[idx] * gain);
                }
            }
        }

        _framePosition += frameCount;
        return (buffer, frameCount);
    }

    private static double ComputeFadeGain(long position, long totalFrames, FadeCurve curve)
    {
        if (totalFrames <= 0) return 1.0;

        // Clamp: this transform is meant to be fed exactly totalFrames frames (CutOptions always
        // arranges that), but it's also public API, so guard against a caller driving more frames
        // through it than that (e.g. via the generic Convert(pipeline) overload with a source longer
        // than totalFrames) -- without this, framesFromEnd goes negative past the end, producing a
        // negative (Linear) or sign-oscillating (EqualPower) gain instead of holding steady at 1.0.
        var t = Math.Clamp((double)position / totalFrames, 0.0, 1.0);
        return curve switch
        {
            FadeCurve.Linear => t,
            FadeCurve.EqualPower => Math.Sin(t * Math.PI / 2),
            _ => t
        };
    }
}

/// <summary>
/// Fade curve shape for fade-in/fade-out transitions.
/// </summary>
public enum FadeCurve
{
    /// <summary>Linear ramp: gain goes from 0 to 1 (or 1 to 0) linearly.</summary>
    Linear,

    /// <summary>Equal-power cosine: gain follows sin(t * π/2), preserving perceived power across the fade.</summary>
    EqualPower
}
