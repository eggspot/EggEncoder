namespace EggEncoder.Pcm;

/// <summary>
/// A steeper low-pass or high-pass filter than a single <see cref="BiquadTransform"/> can provide, built
/// by cascading <c>order / 2</c> Butterworth-Q <see cref="BiquadTransform"/> stages in series -- the same
/// technique ffmpeg's own <c>lowpass</c>/<c>highpass</c> filters use for their <c>poles</c> option beyond
/// the default 2-pole response.
///
/// A single biquad section is a 2-pole (order-2) filter. A Butterworth filter of a higher even order N is
/// realized as N/2 cascaded 2-pole sections sharing the same cutoff frequency, each with a different Q
/// chosen so the combined response is maximally flat in the passband (no ripple) -- the defining property
/// of a Butterworth filter. The per-stage Q comes from splitting the Butterworth pole pattern (poles
/// evenly spaced on the unit circle) into conjugate pairs:
///
///   Q_k = 1 / (2 * cos((2k - 1) * pi / (2 * order))),  for k = 1 .. order/2
///
/// For order = 2 this reduces to a single stage with Q = 1/(2*cos(pi/4)) = 1/sqrt(2) -- exactly
/// BiquadTransform's own default Q, so a 2nd-order ButterworthTransform is identical to a single
/// BiquadTransform LowPass/HighPass at the default Q (verified by test). Higher orders roll off faster
/// (closer to a brick wall) at the cost of more group delay/phase shift near the cutoff.
///
/// Odd orders are not supported: an odd-order Butterworth filter needs one extra first-order (one-pole)
/// section that doesn't fit this type's "cascade of biquads" structure, and ffmpeg's own filters only
/// expose "poles" as 1 or 2 per stage (i.e. order is always a multiple of 2) for the same reason.
///
/// Each stage is a full <see cref="BiquadTransform"/>, so each stage rounds its output to an integer
/// sample before the next stage sees it -- the same boundary every <see cref="IPcmTransform"/> in this
/// pipeline uses between stages (an <see cref="IPcmTransform"/> only ever exchanges <c>int[]</c>).
/// Butterworth filters are typically realized this way in practice too, in preference to deriving one
/// large combined transfer function directly; cascaded low-Q biquad sections are numerically much more
/// stable than a single high-order direct-form filter.
///
/// Only <see cref="BiquadFilterType.LowPass"/> and <see cref="BiquadFilterType.HighPass"/> are supported:
/// the other six <see cref="BiquadFilterType"/> values (band pass, notch, all pass, peaking EQ, and the
/// two shelf types) don't have a standard "higher-order Butterworth cascade" construction in the way
/// low/high pass do, so this type doesn't attempt to generalize to them.
///
/// AOT-safe, managed implementation. Does not change channel count, sample rate, or bit depth.
/// </summary>
public sealed class ButterworthTransform : IPcmTransform
{
    private readonly BiquadTransform[] _stages;

    /// <param name="filterType">Must be <see cref="BiquadFilterType.LowPass"/> or <see cref="BiquadFilterType.HighPass"/>.</param>
    /// <param name="order">Filter order. Must be a positive even integer (2, 4, 6, 8, ...); produces <paramref name="order"/>/2 cascaded biquad stages.</param>
    /// <param name="channels">Number of interleaved channels this transform will process (must match actual input).</param>
    /// <param name="sampleRate">Sample rate in Hz this transform will process (must match actual input).</param>
    /// <param name="frequencyHz">Cutoff frequency in Hz, shared by every cascaded stage. Must be in (0, Nyquist).</param>
    public ButterworthTransform(BiquadFilterType filterType, int order, int channels, int sampleRate, double frequencyHz)
    {
        if (filterType is not (BiquadFilterType.LowPass or BiquadFilterType.HighPass))
            throw new ArgumentException($"ButterworthTransform only supports LowPass/HighPass, but got {filterType}", nameof(filterType));
        if (order <= 0)
            throw new ArgumentOutOfRangeException(nameof(order), order, "Order must be a positive even integer");
        if (order % 2 != 0)
            throw new ArgumentOutOfRangeException(nameof(order), order, $"Order must be even (a cascade of whole biquad sections can't realize an odd order); got {order}");

        var stageCount = order / 2;
        _stages = new BiquadTransform[stageCount];
        for (var k = 1; k <= stageCount; k++)
        {
            var q = 1.0 / (2.0 * Math.Cos(((2 * k) - 1) * Math.PI / (2.0 * order)));

            // Let BiquadTransform's own validation (channels/sampleRate/frequencyHz/q) throw directly --
            // no try/catch here, so a caller sees exactly which parameter and why, not a rewrapped message.
            _stages[k - 1] = new BiquadTransform(filterType, channels, sampleRate, frequencyHz, q);
        }
    }

    public int OutputSampleRate => 0;   // passthrough — preserves input rate
    public int OutputChannels => 0;     // passthrough — preserves input channels
    public int OutputBitsPerSample => 0;
    public bool CanChangeFrameCount => false;

    /// <summary>Reset every cascaded stage's filter history (e.g. to start a new stream with a fresh, silent history).</summary>
    public void Reset()
    {
        foreach (var stage in _stages)
        {
            stage.Reset();
        }
    }

    public (int[] buffer, int frameCount) Apply(int[] buffer, int frameCount, int channels, int sampleRate, int bitsPerSample)
    {
        var currentBuffer = buffer;
        var currentFrameCount = frameCount;

        foreach (var stage in _stages)
        {
            (currentBuffer, currentFrameCount) = stage.Apply(currentBuffer, currentFrameCount, channels, sampleRate, bitsPerSample);
        }

        return (currentBuffer, currentFrameCount);
    }
}
