namespace EggEncoder.Pcm;

/// <summary>
/// Dynamic range compression: once the signal's smoothed envelope exceeds a threshold (dBFS), gain
/// reduction is applied proportional to how far over threshold it is, scaled by 1/ratio -- the
/// standard feedforward compressor design (threshold/ratio/attack/release/makeup gain).
///
/// Stereo-linked: the envelope follower tracks a single scalar -- the loudest absolute sample across
/// every channel of each frame, not independent per-channel envelopes -- so a transient in one
/// channel doesn't shift the perceived stereo image by compressing only that channel. This also means
/// (unlike <see cref="BiquadTransform"/>/<see cref="FirFilterTransform"/>, which need per-channel
/// filter history sized at construction) this transform works with whatever channel count
/// <see cref="Apply"/> is actually given, the same channel-count-agnostic shape
/// <see cref="FadeTransform"/> already has.
///
/// The envelope is tracked in linear scale (cheaper than converting every sample to dB just to
/// smooth it, only converting once per frame for the actual threshold/reduction computation) via a
/// one-pole filter with separate attack and release time constants: it moves toward a louder instant
/// peak using the attack coefficient, and toward a quieter one using the release coefficient --
/// standard asymmetric peak-detector behavior. An attack/release of 0 ms degrades cleanly to an
/// instant response: dividing by zero milliseconds produces positive infinity (IEEE 754 double
/// division, not a thrown exception), <c>exp(-infinity) == 0</c>, and a coefficient of 0 makes the
/// envelope snap directly to the instant peak with no smoothing at all -- no special-casing needed.
///
/// AOT-safe, managed implementation. Does not change channel count, sample rate, or bit depth.
/// </summary>
public sealed class CompressorTransform : IPcmTransform
{
    private readonly int _sampleRate;
    private readonly double _thresholdDb;
    private readonly double _ratio;
    private readonly double _makeupGainDb;
    private readonly double _attackCoeff;
    private readonly double _releaseCoeff;

    private double _envelopeLinear;

    /// <param name="sampleRate">Sample rate in Hz this transform will process (must match actual input).</param>
    /// <param name="thresholdDb">Level above which gain reduction begins, in dBFS. Must be &lt;= 0.</param>
    /// <param name="ratio">Input/output ratio above threshold, e.g. 4.0 means 4 dB in becomes 1 dB out. Must be &gt;= 1.0 (1.0 is a no-op -- no reduction at any level).</param>
    /// <param name="attackMs">Time constant for the envelope to catch up to a louder peak, in milliseconds. Must be &gt;= 0 (0 is an instant response).</param>
    /// <param name="releaseMs">Time constant for the envelope to settle back down after a quieter peak, in milliseconds. Must be &gt;= 0 (0 is an instant response).</param>
    /// <param name="makeupGainDb">Flat gain applied after compression, in dB, to compensate for the overall level reduction. Must be finite; may be negative.</param>
    public CompressorTransform(int sampleRate, double thresholdDb, double ratio, double attackMs, double releaseMs, double makeupGainDb = 0.0)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be positive");
        if (double.IsNaN(thresholdDb) || thresholdDb > 0)
            throw new ArgumentOutOfRangeException(nameof(thresholdDb), thresholdDb, "Threshold must be <= 0 dBFS");
        if (double.IsNaN(ratio) || ratio < 1.0)
            throw new ArgumentOutOfRangeException(nameof(ratio), ratio, "Ratio must be >= 1.0");
        if (double.IsNaN(attackMs) || attackMs < 0)
            throw new ArgumentOutOfRangeException(nameof(attackMs), attackMs, "Attack time must be non-negative");
        if (double.IsNaN(releaseMs) || releaseMs < 0)
            throw new ArgumentOutOfRangeException(nameof(releaseMs), releaseMs, "Release time must be non-negative");
        if (!double.IsFinite(makeupGainDb))
            throw new ArgumentOutOfRangeException(nameof(makeupGainDb), makeupGainDb, "Makeup gain must be a finite number");

        _sampleRate = sampleRate;
        _thresholdDb = thresholdDb;
        _ratio = ratio;
        _makeupGainDb = makeupGainDb;
        _attackCoeff = ComputeCoefficient(sampleRate, attackMs);
        _releaseCoeff = ComputeCoefficient(sampleRate, releaseMs);
    }

    // Standard one-pole time-constant-to-coefficient formula. See this type's own doc comment for why
    // timeMs == 0 needs no special case: it flows through to a coefficient of exactly 0 via IEEE 754
    // double division (+Infinity, never a thrown exception) and Math.Exp(-Infinity) == 0.
    private static double ComputeCoefficient(int sampleRate, double timeMs) => Math.Exp(-1.0 / (sampleRate * (timeMs / 1000.0)));

    public int OutputSampleRate => 0;
    public int OutputChannels => 0;
    public int OutputBitsPerSample => 0;
    public bool CanChangeFrameCount => false;

    /// <summary>Reset the envelope follower (e.g. to start a new stream with a fresh, silent envelope).</summary>
    public void Reset() => _envelopeLinear = 0.0;

    public (int[] buffer, int frameCount) Apply(int[] buffer, int frameCount, int channels, int sampleRate, int bitsPerSample)
    {
        if (sampleRate != _sampleRate)
            throw new ArgumentException($"Expected {_sampleRate} Hz but received {sampleRate} Hz", nameof(sampleRate));
        if (frameCount <= 0)
            return (buffer, frameCount);

        var (minValue, maxValue) = BitDepthFormatTransform.GetNativeRange(bitsPerSample);

        for (var frame = 0; frame < frameCount; frame++)
        {
            var baseIndex = frame * channels;

            var instantPeak = 0.0;
            for (var channel = 0; channel < channels; channel++)
            {
                var abs = Math.Abs((double)buffer[baseIndex + channel]);
                if (abs > instantPeak)
                {
                    instantPeak = abs;
                }
            }

            var coeff = instantPeak > _envelopeLinear ? _attackCoeff : _releaseCoeff;
            _envelopeLinear = (coeff * _envelopeLinear) + ((1.0 - coeff) * instantPeak);

            // Guarded rather than letting log10(0) produce -Infinity directly: silence is always below
            // any <= 0 dBFS threshold anyway (reductionDb would come out 0 either way), this just
            // avoids computing an actual -Infinity intermediate value.
            var envelopeDb = _envelopeLinear <= 0 ? double.NegativeInfinity : 20.0 * Math.Log10(_envelopeLinear / maxValue);
            var reductionDb = envelopeDb > _thresholdDb ? (envelopeDb - _thresholdDb) * (1.0 - (1.0 / _ratio)) : 0.0;
            var gain = Math.Pow(10.0, (_makeupGainDb - reductionDb) / 20.0);

            for (var channel = 0; channel < channels; channel++)
            {
                var index = baseIndex + channel;
                buffer[index] = (int)Math.Clamp(buffer[index] * gain, minValue, maxValue);
            }
        }

        return (buffer, frameCount);
    }
}
