namespace EggEncoder.Pcm;

/// <summary>
/// A general finite impulse response (FIR) filter: convolves every channel with the same caller-supplied
/// tap array, applied identically per channel. This is ffmpeg <c>afir</c>/general-FIR parity -- unlike
/// <see cref="BiquadTransform"/>/<see cref="ButterworthTransform"/>, which only produce the specific
/// filter shapes their own formulas define, this type accepts arbitrary taps, so a caller can realize any
/// FIR response (an equalizer design, a custom window, taps exported from another tool, etc.) without
/// this type needing any filter-design code of its own. The raw-taps constructor is deliberately the only
/// entry point: generality -- accepting whatever taps a caller already has -- is the entire point of this
/// type, so there's no convenience "design a low-pass" factory layered on top here (that's what
/// <see cref="BiquadTransform"/>/<see cref="ButterworthTransform"/> already cover for the common low/high
/// pass case).
///
/// For taps <c>h[0..M-1]</c>, the output at sample <c>n</c> is the direct-form convolution
///
///   y[n] = sum_{k=0}^{M-1} h[k] * x[n-k]
///
/// which needs the <c>M-1</c> samples immediately before <c>n</c>. For <c>n</c> at or near the start of a
/// block, those samples are from the *previous* block, so this type keeps a per-channel history of the
/// last <c>M-1</c> input samples across <see cref="Apply"/> calls (the same cross-block-correctness bar
/// <see cref="ResamplingTransform"/>/<see cref="BiquadTransform"/> hold themselves to) -- filtering is
/// correct and identical regardless of how the stream is split into blocks. Each channel's history is
/// independent, since taps are applied identically per channel but channels carry unrelated signals.
///
/// AOT-safe, managed implementation (direct-form convolution, no FFT). Does not change channel count,
/// sample rate, or bit depth.
/// </summary>
public sealed class FirFilterTransform : IPcmTransform
{
    private readonly int _channels;
    private readonly double[] _taps;
    private readonly int _historyLength;
    private readonly double[][] _history;
    private double[] _extendedScratch = [];

    /// <param name="channels">Number of interleaved channels this transform will process (must match actual input).</param>
    /// <param name="taps">
    /// Filter coefficients <c>h[0..M-1]</c>, applied identically to every channel: <c>taps[0]</c> weights
    /// the current sample, <c>taps[1]</c> the previous sample, and so on. Must be non-null, non-empty, and
    /// every value finite. Copied defensively, so mutating the caller's array afterward has no effect.
    /// </param>
    public FirFilterTransform(int channels, double[] taps)
    {
        if (channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channels must be positive");

        ArgumentNullException.ThrowIfNull(taps);
        if (taps.Length == 0)
            throw new ArgumentException("Taps must not be empty", nameof(taps));

        for (var i = 0; i < taps.Length; i++)
        {
            if (double.IsNaN(taps[i]) || double.IsInfinity(taps[i]))
                throw new ArgumentOutOfRangeException(nameof(taps), taps[i], $"Tap at index {i} must be finite, but was {taps[i]}");
        }

        _channels = channels;
        _taps = (double[])taps.Clone();
        _historyLength = _taps.Length - 1;

        _history = new double[channels][];
        for (var ch = 0; ch < channels; ch++)
        {
            _history[ch] = new double[_historyLength];
        }
    }

    public int OutputSampleRate => 0;   // passthrough — preserves input rate
    public int OutputChannels => 0;     // passthrough — preserves input channels
    public int OutputBitsPerSample => 0;
    public bool CanChangeFrameCount => false;

    /// <summary>Reset every channel's history to silence (e.g. to start a new stream with a fresh, silent history).</summary>
    public void Reset()
    {
        foreach (var history in _history)
        {
            Array.Clear(history);
        }
    }

    public (int[] buffer, int frameCount) Apply(int[] buffer, int frameCount, int channels, int sampleRate, int bitsPerSample)
    {
        if (channels != _channels)
            throw new ArgumentException($"Expected {_channels} channels but received {channels}", nameof(channels));
        if (frameCount <= 0)
            return (buffer, frameCount);

        var (minValue, maxValue) = BitDepthFormatTransform.GetNativeRange(bitsPerSample);

        // "extended" is this channel's last _historyLength samples (from previous calls) followed by this
        // block's samples for that channel -- i.e. exactly the window of past samples every output sample
        // in this block could possibly need, laid out so convolution never has to branch between "still in
        // history" and "now in this block". Reused across channels and calls (grown, never shrunk), like
        // AudioCutter's ScratchBuffer, so a steady-state stream doesn't allocate once it reaches its
        // largest block size.
        var extendedLength = _historyLength + frameCount;
        if (_extendedScratch.Length < extendedLength)
        {
            _extendedScratch = new double[extendedLength];
        }

        var extended = _extendedScratch;

        for (var ch = 0; ch < channels; ch++)
        {
            var history = _history[ch];

            Array.Copy(history, extended, _historyLength);
            for (var i = 0; i < frameCount; i++)
            {
                extended[_historyLength + i] = buffer[(i * channels) + ch];
            }

            for (var i = 0; i < frameCount; i++)
            {
                var sum = 0.0;
                var baseIndex = _historyLength + i;
                for (var k = 0; k < _taps.Length; k++)
                {
                    sum += _taps[k] * extended[baseIndex - k];
                }

                // Every tap is validated finite at construction, but the running sum itself isn't: an
                // extreme enough tap magnitude can still overflow an individual term to +-Infinity, and
                // if two such terms land on opposite signs, Infinity + -Infinity = NaN. Math.Clamp passes
                // NaN through unchanged (every comparison against it is false), so without this check a
                // NaN sum would reach the (int) cast, whose result for NaN is unspecified by the C# spec.
                // Infinity alone doesn't need this: Math.Clamp already saturates +-Infinity to max/min
                // correctly, since those comparisons are well-defined.
                if (double.IsNaN(sum))
                {
                    sum = 0.0;
                }

                buffer[(i * channels) + ch] = (int)Math.Clamp(Math.Round(sum), minValue, maxValue);
            }

            // The next call's history is this call's last _historyLength samples -- from either this
            // block (if it was at least that long) or a mix of this block and the previous history (if
            // not); "the last _historyLength entries of extended" is correct either way without needing to
            // special-case which.
            if (_historyLength > 0)
            {
                Array.Copy(extended, frameCount, history, 0, _historyLength);
            }
        }

        return (buffer, frameCount);
    }
}
