namespace EggEncoder.Pcm;

/// <summary>
/// A single second-order IIR filter section ("biquad") using the standard coefficient formulas from
/// Robert Bristow-Johnson's "Audio EQ Cookbook". Covers the eight standard filter types: low/high pass,
/// band pass, notch, all pass, peaking EQ, and low/high shelf.
///
/// Implemented as Direct Form II Transposed: two state values per channel (w1, w2), updated as
///   y[n]  = b0*x[n] + w1
///   w1'   = b1*x[n] - a1*y[n] + w2
///   w2'   = b2*x[n] - a2*y[n]
/// (coefficients already normalized by a0). This form is the standard choice for production biquads:
/// lower coefficient-quantization sensitivity than Direct Form I/II, and only two state values per
/// channel rather than four.
///
/// The constructor's <c>q</c> argument means different things depending on filter type, matching the
/// cookbook: it's the quality factor Q for every type
/// except <see cref="BiquadFilterType.LowShelf"/>/<see cref="BiquadFilterType.HighShelf"/>, where it's
/// the shelf slope S (valid range (0, 1], where 1.0 is the steepest slope with no overshoot). A caller
/// who has a bandwidth in octaves instead of Q can convert via the standard formula
/// <c>Q = 1 / (2 * sinh(ln(2)/2 * BW * w0/sin(w0)))</c>; this type doesn't take bandwidth directly to
/// avoid a third input mode for what the cookbook itself treats as an alternate parameterization of Q.
///
/// AOT-safe, managed implementation. Does not change channel count, sample rate, or bit depth.
/// </summary>
public sealed class BiquadTransform : IPcmTransform
{
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly double _b0;
    private readonly double _b1;
    private readonly double _b2;
    private readonly double _a1;
    private readonly double _a2;
    private readonly double[] _w1;
    private readonly double[] _w2;

    /// <param name="filterType">Which of the eight standard biquad filter responses to apply.</param>
    /// <param name="channels">Number of interleaved channels this transform will process (must match actual input).</param>
    /// <param name="sampleRate">Sample rate in Hz this transform will process (must match actual input).</param>
    /// <param name="frequencyHz">Center frequency (band pass/notch/peaking) or cutoff frequency (low/high pass/shelf), in Hz. Must be in (0, Nyquist).</param>
    /// <param name="q">
    /// Quality factor for every type except LowShelf/HighShelf, where this is instead the shelf slope S
    /// in (0, 1]. Must be positive. Defaults to 1/sqrt(2) (~0.7071), the standard "maximally flat"
    /// Butterworth Q for low/high pass and a no-overshoot-adjacent shelf slope.
    /// </param>
    /// <param name="gainDb">Boost/cut in decibels for PeakingEq/LowShelf/HighShelf; must be 0 for every other filter type, which don't have a gain concept.</param>
    public BiquadTransform(BiquadFilterType filterType, int channels, int sampleRate, double frequencyHz, double q = 0.7071067811865476, double gainDb = 0.0)
    {
        if (channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channels must be positive");
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be positive");
        if (double.IsNaN(frequencyHz) || frequencyHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(frequencyHz), frequencyHz, "Frequency must be positive");

        var nyquist = sampleRate / 2.0;
        if (frequencyHz >= nyquist)
            throw new ArgumentOutOfRangeException(nameof(frequencyHz), frequencyHz, $"Frequency must be below the Nyquist frequency ({nyquist} Hz for a {sampleRate} Hz sample rate)");

        if (double.IsNaN(q) || q <= 0)
            throw new ArgumentOutOfRangeException(nameof(q), q, "Q (or shelf slope) must be positive");

        var isShelf = filterType is BiquadFilterType.LowShelf or BiquadFilterType.HighShelf;
        if (isShelf)
        {
            if (q > 1.0)
                throw new ArgumentOutOfRangeException(nameof(q), q, "Shelf slope must be in (0, 1] for LowShelf/HighShelf");
        }
        else if (filterType != BiquadFilterType.PeakingEq && gainDb != 0.0)
        {
            throw new ArgumentException($"gainDb only applies to PeakingEq/LowShelf/HighShelf, but a non-zero value ({gainDb}) was given for {filterType}", nameof(gainDb));
        }

        if (double.IsNaN(gainDb) || double.IsInfinity(gainDb))
            throw new ArgumentOutOfRangeException(nameof(gainDb), gainDb, "Gain must be a finite number");

        _channels = channels;
        _sampleRate = sampleRate;
        (_b0, _b1, _b2, _a1, _a2) = ComputeCoefficients(filterType, sampleRate, frequencyHz, q, gainDb);
        _w1 = new double[channels];
        _w2 = new double[channels];
    }

    public int OutputSampleRate => 0;   // passthrough — preserves input rate
    public int OutputChannels => 0;     // passthrough — preserves input channels
    public int OutputBitsPerSample => 0;
    public bool CanChangeFrameCount => false;

    /// <summary>Reset the per-channel filter state (e.g. to start a new stream with a fresh, silent filter history).</summary>
    public void Reset()
    {
        Array.Clear(_w1);
        Array.Clear(_w2);
    }

    public (int[] buffer, int frameCount) Apply(int[] buffer, int frameCount, int channels, int sampleRate, int bitsPerSample)
    {
        if (channels != _channels)
            throw new ArgumentException($"Expected {_channels} channels but received {channels}", nameof(channels));
        if (sampleRate != _sampleRate)
            throw new ArgumentException($"Expected {_sampleRate} Hz but received {sampleRate} Hz", nameof(sampleRate));
        if (frameCount <= 0)
            return (buffer, frameCount);

        var (minValue, maxValue) = BitDepthFormatTransform.GetNativeRange(bitsPerSample);

        for (var frame = 0; frame < frameCount; frame++)
        {
            var frameStart = frame * channels;
            for (var ch = 0; ch < channels; ch++)
            {
                var idx = frameStart + ch;
                double x0 = buffer[idx];

                var y0 = (_b0 * x0) + _w1[ch];
                _w1[ch] = (_b1 * x0) - (_a1 * y0) + _w2[ch];
                _w2[ch] = (_b2 * x0) - (_a2 * y0);

                buffer[idx] = (int)Math.Clamp(Math.Round(y0), minValue, maxValue);
            }
        }

        return (buffer, frameCount);
    }

    // RBJ Audio EQ Cookbook formulas (https://www.w3.org/TR/audio-eq-cookbook/), returning coefficients
    // already normalized by a0 (i.e. b0/a0, b1/a0, b2/a0, a1/a0, a2/a0) for direct use in Direct Form II
    // Transposed, which has no separate a0 term.
    private static (double b0, double b1, double b2, double a1, double a2) ComputeCoefficients(
        BiquadFilterType filterType, double sampleRate, double frequencyHz, double q, double gainDb)
    {
        var w0 = 2.0 * Math.PI * frequencyHz / sampleRate;
        var cosW0 = Math.Cos(w0);
        var sinW0 = Math.Sin(w0);
        var a = Math.Pow(10.0, gainDb / 40.0); // linear amplitude for peaking/shelf gain

        double alpha;
        if (filterType is BiquadFilterType.LowShelf or BiquadFilterType.HighShelf)
        {
            // q is the shelf slope S here; S == 1 is the steepest slope without overshoot.
            var s = q;
            alpha = (sinW0 / 2.0) * Math.Sqrt(((a + (1.0 / a)) * ((1.0 / s) - 1.0)) + 2.0);
        }
        else
        {
            alpha = sinW0 / (2.0 * q);
        }

        double b0, b1, b2, a0, a1, a2;

        switch (filterType)
        {
            case BiquadFilterType.LowPass:
                b0 = (1 - cosW0) / 2;
                b1 = 1 - cosW0;
                b2 = (1 - cosW0) / 2;
                a0 = 1 + alpha;
                a1 = -2 * cosW0;
                a2 = 1 - alpha;
                break;

            case BiquadFilterType.HighPass:
                b0 = (1 + cosW0) / 2;
                b1 = -(1 + cosW0);
                b2 = (1 + cosW0) / 2;
                a0 = 1 + alpha;
                a1 = -2 * cosW0;
                a2 = 1 - alpha;
                break;

            case BiquadFilterType.BandPass:
                // Constant 0 dB peak gain variant (peak gain is always 1.0 at the center frequency,
                // regardless of Q -- the alternative "constant skirt gain" cookbook variant instead
                // keeps skirt gain constant and lets peak gain scale with Q).
                b0 = alpha;
                b1 = 0;
                b2 = -alpha;
                a0 = 1 + alpha;
                a1 = -2 * cosW0;
                a2 = 1 - alpha;
                break;

            case BiquadFilterType.Notch:
                b0 = 1;
                b1 = -2 * cosW0;
                b2 = 1;
                a0 = 1 + alpha;
                a1 = -2 * cosW0;
                a2 = 1 - alpha;
                break;

            case BiquadFilterType.AllPass:
                b0 = 1 - alpha;
                b1 = -2 * cosW0;
                b2 = 1 + alpha;
                a0 = 1 + alpha;
                a1 = -2 * cosW0;
                a2 = 1 - alpha;
                break;

            case BiquadFilterType.PeakingEq:
                b0 = 1 + (alpha * a);
                b1 = -2 * cosW0;
                b2 = 1 - (alpha * a);
                a0 = 1 + (alpha / a);
                a1 = -2 * cosW0;
                a2 = 1 - (alpha / a);
                break;

            case BiquadFilterType.LowShelf:
            {
                var sqrtA2Alpha = 2 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1) - ((a - 1) * cosW0) + sqrtA2Alpha);
                b1 = 2 * a * ((a - 1) - ((a + 1) * cosW0));
                b2 = a * ((a + 1) - ((a - 1) * cosW0) - sqrtA2Alpha);
                a0 = (a + 1) + ((a - 1) * cosW0) + sqrtA2Alpha;
                a1 = -2 * ((a - 1) + ((a + 1) * cosW0));
                a2 = (a + 1) + ((a - 1) * cosW0) - sqrtA2Alpha;
                break;
            }

            case BiquadFilterType.HighShelf:
            {
                var sqrtA2Alpha = 2 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1) + ((a - 1) * cosW0) + sqrtA2Alpha);
                b1 = -2 * a * ((a - 1) + ((a + 1) * cosW0));
                b2 = a * ((a + 1) + ((a - 1) * cosW0) - sqrtA2Alpha);
                a0 = (a + 1) - ((a - 1) * cosW0) + sqrtA2Alpha;
                a1 = 2 * ((a - 1) - ((a + 1) * cosW0));
                a2 = (a + 1) - ((a - 1) * cosW0) - sqrtA2Alpha;
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(filterType), filterType, "Unsupported filter type");
        }

        return (b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }
}

/// <summary>Standard biquad filter responses (RBJ Audio EQ Cookbook).</summary>
public enum BiquadFilterType
{
    /// <summary>Attenuates above the cutoff frequency.</summary>
    LowPass,

    /// <summary>Attenuates below the cutoff frequency.</summary>
    HighPass,

    /// <summary>Passes a band around the center frequency; 0 dB peak gain at center.</summary>
    BandPass,

    /// <summary>Attenuates a narrow band around the center frequency (band-reject).</summary>
    Notch,

    /// <summary>Unity gain at every frequency; shifts phase only.</summary>
    AllPass,

    /// <summary>Boosts or cuts a band around the center frequency by <c>gainDb</c>, unity gain elsewhere.</summary>
    PeakingEq,

    /// <summary>Boosts or cuts frequencies below the cutoff by <c>gainDb</c>, unity gain above it.</summary>
    LowShelf,

    /// <summary>Boosts or cuts frequencies above the cutoff by <c>gainDb</c>, unity gain below it.</summary>
    HighShelf
}
