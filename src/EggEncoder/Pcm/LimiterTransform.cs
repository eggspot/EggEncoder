namespace EggEncoder.Pcm;

/// <summary>
/// Peak limiting: an infinite-ratio compressor (the smoothed envelope is clamped exactly AT the
/// ceiling once it exceeds it, not merely nudged toward it by some finite ratio the way
/// <see cref="CompressorTransform"/>/<see cref="NoiseGateTransform"/> are) plus a final per-sample
/// hard clip to the ceiling.
///
/// The hard clip is what makes this a true peak limiter rather than just "CompressorTransform with a
/// very high ratio": a nonzero attack time lets a fast transient slip past the smoothed envelope
/// follower before gain reduction has fully caught up to it, so relying on the envelope-derived gain
/// alone cannot *guarantee* the output never exceeds the ceiling -- only that it is pushed toward it.
/// The hard clip turns that into an exact guarantee, independent of attack time.
///
/// Stereo-linked and channel-count-agnostic in exactly the same way
/// <see cref="CompressorTransform"/>/<see cref="NoiseGateTransform"/> are: the envelope follower
/// tracks a single scalar -- the loudest absolute sample across every channel of each frame -- so a
/// transient in one channel doesn't shift the stereo image by reducing only that channel, and this
/// transform works with whatever channel count <see cref="Apply"/> is actually given rather than
/// needing per-channel state sized at construction.
///
/// AOT-safe, managed implementation. Does not change channel count, sample rate, or bit depth.
/// </summary>
public sealed class LimiterTransform : IPcmTransform
{
    private readonly int _sampleRate;
    private readonly double _ceilingLinearRatio; // 10^(ceilingDb/20), in (0, 1]
    private readonly double _attackCoeff;
    private readonly double _releaseCoeff;

    private double _envelopeLinear;

    /// <param name="sampleRate">Sample rate in Hz this transform will process (must match actual input).</param>
    /// <param name="ceilingDb">The output never exceeds this level, in dBFS. Must be finite and &lt;= 0.</param>
    /// <param name="releaseMs">Time constant for the envelope to settle back down after a quieter peak, in milliseconds. Must be &gt;= 0 (0 is an instant response).</param>
    /// <param name="attackMs">Time constant for the envelope to catch up to a louder peak, in milliseconds. Must be &gt;= 0; defaults to 0 (instant response), the classic brick-wall-limiter default -- a nonzero value trades a guaranteed-clean ceiling crossing for a softer-sounding, but still hard-clipped, reduction.</param>
    public LimiterTransform(int sampleRate, double ceilingDb, double releaseMs, double attackMs = 0.0)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be positive");
        if (!double.IsFinite(ceilingDb) || ceilingDb > 0)
            throw new ArgumentOutOfRangeException(nameof(ceilingDb), ceilingDb, "Ceiling must be a finite number <= 0 dBFS");
        if (double.IsNaN(attackMs) || attackMs < 0)
            throw new ArgumentOutOfRangeException(nameof(attackMs), attackMs, "Attack time must be non-negative");
        if (double.IsNaN(releaseMs) || releaseMs < 0)
            throw new ArgumentOutOfRangeException(nameof(releaseMs), releaseMs, "Release time must be non-negative");

        _sampleRate = sampleRate;
        _ceilingLinearRatio = Math.Pow(10.0, ceilingDb / 20.0);
        _attackCoeff = ComputeCoefficient(sampleRate, attackMs);
        _releaseCoeff = ComputeCoefficient(sampleRate, releaseMs);
    }

    // Standard one-pole time-constant-to-coefficient formula; see CompressorTransform's own doc
    // comment for why timeMs == 0 needs no special case.
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
        var ceilingValue = _ceilingLinearRatio * maxValue;
        var clampMin = Math.Max(minValue, -ceilingValue);
        var clampMax = Math.Min(maxValue, ceilingValue);

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

            // Infinite-ratio compression: once the smoothed envelope exceeds the ceiling, the gain
            // applied brings it exactly down to the ceiling -- not some ratio-scaled fraction of the
            // way there.
            var gain = _envelopeLinear > ceilingValue ? ceilingValue / _envelopeLinear : 1.0;

            for (var channel = 0; channel < channels; channel++)
            {
                var index = baseIndex + channel;
                // The envelope-derived gain above is this transform's main reduction; the final clamp
                // to the ceiling is what guarantees no single sample -- including one that arrived
                // before a nonzero attack time let the envelope catch up -- can exceed it.
                buffer[index] = (int)Math.Clamp(buffer[index] * gain, clampMin, clampMax);
            }
        }

        return (buffer, frameCount);
    }
}
