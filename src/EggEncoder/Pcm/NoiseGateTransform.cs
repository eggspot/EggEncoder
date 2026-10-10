namespace EggEncoder.Pcm;

/// <summary>
/// Downward expansion / noise gating: once the signal's smoothed envelope drops below a threshold
/// (dBFS), gain reduction is applied proportional to how far under threshold it is, scaled by
/// 1/ratio -- the mirror image of <see cref="CompressorTransform"/>'s own feedforward design, gating
/// on the opposite side of the threshold instead of compressing above it.
///
/// Stereo-linked and channel-count-agnostic in exactly the same way <see cref="CompressorTransform"/>
/// is: the envelope follower tracks a single scalar -- the loudest absolute sample across every
/// channel of each frame -- so a quiet passage in only one channel doesn't shift the stereo image by
/// gating just that channel, and this transform works with whatever channel count <see cref="Apply"/>
/// is actually given rather than needing per-channel state sized at construction.
///
/// The envelope is tracked in linear scale via the same one-pole attack/release filter
/// <see cref="CompressorTransform"/> uses (attack governs the gate opening back up toward a louder
/// peak, release governs it closing back down toward a quieter one), converted to dB once per frame
/// for the reduction computation.
///
/// Unlike <see cref="CompressorTransform"/>, true digital silence (envelope == 0, <c>envelopeDb ==
/// -Infinity</c>) is the gate's own common case, not a boundary condition -- so the reduction formula
/// can't reuse the compressor's own "finite diff times 1/ratio" shape unguarded: with
/// <c>ratio == 1.0</c> (meaning "no gating effect"), <c>(threshold - (-Infinity)) * (1 - 1/ratio)</c>
/// is <c>Infinity * 0</c>, which is <see cref="double.NaN"/> under IEEE 754, not 0. This is handled
/// explicitly (see <see cref="Apply"/>) rather than discovered the first time a caller gates a stream
/// that starts with real silence.
///
/// AOT-safe, managed implementation. Does not change channel count, sample rate, or bit depth.
/// </summary>
public sealed class NoiseGateTransform : IPcmTransform
{
    private readonly int _sampleRate;
    private readonly double _thresholdDb;
    private readonly double _ratio;
    private readonly double _attackCoeff;
    private readonly double _releaseCoeff;

    private double _envelopeLinear;

    /// <param name="sampleRate">Sample rate in Hz this transform will process (must match actual input).</param>
    /// <param name="thresholdDb">Level below which gain reduction begins, in dBFS. Must be &lt;= 0.</param>
    /// <param name="ratio">Downward expansion ratio below threshold, e.g. 4.0 means every 1 dB under threshold becomes 4 dB of reduction. Must be &gt;= 1.0 (1.0 is a no-op -- no reduction at any level).</param>
    /// <param name="attackMs">Time constant for the envelope to catch up to a louder peak (the gate opening), in milliseconds. Must be &gt;= 0 (0 is an instant response).</param>
    /// <param name="releaseMs">Time constant for the envelope to settle back down after a quieter peak (the gate closing), in milliseconds. Must be &gt;= 0 (0 is an instant response).</param>
    public NoiseGateTransform(int sampleRate, double thresholdDb, double ratio, double attackMs, double releaseMs)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be positive");
        // double.IsFinite rejects -Infinity as well as NaN. Unlike CompressorTransform's own
        // thresholdDb, -Infinity here can't actually produce a NaN (Apply's `envelopeDb >=
        // _thresholdDb` short-circuit is always true against a -Infinity threshold, so the gate
        // becomes a permanent, silent no-op before the reduction formula ever runs) -- but a gate
        // that can never gate is a confusing way to express "disabled" when `ratio == 1.0` already
        // says that explicitly, so this is rejected for the same validation shape as the compressor's
        // mirror-image parameter, not because -Infinity is independently unsafe here.
        if (!double.IsFinite(thresholdDb) || thresholdDb > 0)
            throw new ArgumentOutOfRangeException(nameof(thresholdDb), thresholdDb, "Threshold must be a finite number <= 0 dBFS");
        if (double.IsNaN(ratio) || ratio < 1.0)
            throw new ArgumentOutOfRangeException(nameof(ratio), ratio, "Ratio must be >= 1.0");
        if (double.IsNaN(attackMs) || attackMs < 0)
            throw new ArgumentOutOfRangeException(nameof(attackMs), attackMs, "Attack time must be non-negative");
        if (double.IsNaN(releaseMs) || releaseMs < 0)
            throw new ArgumentOutOfRangeException(nameof(releaseMs), releaseMs, "Release time must be non-negative");

        _sampleRate = sampleRate;
        _thresholdDb = thresholdDb;
        _ratio = ratio;
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

            var envelopeDb = _envelopeLinear <= 0 ? double.NegativeInfinity : 20.0 * Math.Log10(_envelopeLinear / maxValue);

            double reductionDb;
            if (envelopeDb >= _thresholdDb || _ratio == 1.0)
            {
                // Above/at threshold: no gating. Also covers ratio == 1.0 (an explicit no-op
                // regardless of level) up front, before the formula below ever runs it against a
                // -Infinity envelopeDb -- see this type's own doc comment for why that combination
                // would otherwise produce Infinity * 0 == NaN rather than 0.
                reductionDb = 0.0;
            }
            else if (double.IsNegativeInfinity(envelopeDb))
            {
                // True digital silence with an actual (> 1.0) ratio: full reduction. Math.Pow's own
                // 10^(-Infinity) below evaluates to exactly 0 (IEEE 754), not NaN, so this doesn't
                // need its own further special-casing past this point.
                reductionDb = double.PositiveInfinity;
            }
            else
            {
                reductionDb = (_thresholdDb - envelopeDb) * (1.0 - (1.0 / _ratio));
            }

            var gain = Math.Pow(10.0, -reductionDb / 20.0);

            for (var channel = 0; channel < channels; channel++)
            {
                var index = baseIndex + channel;
                buffer[index] = (int)Math.Clamp(buffer[index] * gain, minValue, maxValue);
            }
        }

        return (buffer, frameCount);
    }
}
