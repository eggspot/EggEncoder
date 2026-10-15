using EggEncoder.Waveform;

namespace EggEncoder.Pcm;

/// <summary>
/// Loudness-normalizes audio to a target LUFS (ITU-R BS.1770-4 / EBU R128 integrated loudness),
/// the real perceptual-loudness counterpart to <see cref="PeakNormalizationTransform"/>'s own
/// peak-dBFS target -- the two serve genuinely different goals (matching real-world mastering/
/// distribution tooling's own distinction between peak and loudness normalization), not two ways
/// of asking the same question.
///
/// Unlike <see cref="PeakNormalizationTransform"/>, this transform has no "self-measure from
/// whatever buffer <see cref="Apply"/> is first given" fallback: EBU R128's own gating algorithm
/// (<see cref="WaveformCalculator.GetIntegratedLoudness"/>) genuinely needs the whole signal, not
/// just one ~4096-frame decode block, to produce a meaningful number -- a block-local "measurement"
/// would silently normalize against the wrong value far more often than peak normalization's own
/// same shortcut would, so <see cref="MeasureLoudness"/> must be called with a true whole-file
/// measurement (see <c>AudioCutter.MeasureIntegratedLoudness</c>) before this transform is ever
/// handed to a pipeline; <see cref="Apply"/> throws rather than guessing.
///
/// AOT-safe, managed, allocation-free (in-place mutation) after measurement.
/// </summary>
public sealed class LoudnessTransform : IPcmTransform
{
    private readonly double _targetLufs;
    private double? _measuredLufs;
    private bool _measured;

    /// <param name="targetLufs">Target integrated loudness in LUFS, e.g. -14.0 (a common streaming-platform target) or -23.0 (EBU R128 broadcast). Any finite value is accepted -- the standard's own LUFS scale has no fixed upper or lower bound this type enforces.</param>
    public LoudnessTransform(double targetLufs)
    {
        // double.IsFinite rejects NaN and +/-Infinity -- a "target" of either is not a meaningful
        // request, and Math.Pow(10, Infinity / 20.0) would otherwise silently produce an infinite
        // or NaN gain that corrupts every sample Apply touches.
        if (!double.IsFinite(targetLufs)) throw new ArgumentOutOfRangeException(nameof(targetLufs), targetLufs, "Target LUFS must be a finite number");
        _targetLufs = targetLufs;
    }

    /// <summary>
    /// Fixes this transform's gain from an already-measured whole-file integrated loudness (e.g.
    /// from <c>AudioCutter.MeasureIntegratedLoudness</c> or <see cref="WaveformCalculator.GetIntegratedLoudness"/>
    /// run over the complete decoded signal) -- this is the only correct way to feed this transform
    /// a measurement, since <see cref="Apply"/> itself never self-measures.
    /// </summary>
    /// <param name="measuredLufs">
    /// The source's own true integrated loudness in LUFS, or <see langword="null"/> if it couldn't
    /// be measured at all (e.g. audio shorter than EBU R128's own minimum 400ms gating block --
    /// see <see cref="WaveformCalculator.GetIntegratedLoudness"/>'s own doc comment). Can
    /// legitimately be <see cref="double.NegativeInfinity"/> for a source that's entirely at or
    /// below the standard's own -70 LUFS absolute silence gate; both <see langword="null"/> and
    /// <see cref="double.NegativeInfinity"/> make <see cref="Apply"/> pass samples through
    /// unchanged, since no finite gain could bring either case to a finite target.
    /// </param>
    public void MeasureLoudness(double? measuredLufs)
    {
        if (_measured) return;
        _measuredLufs = measuredLufs;
        _measured = true;
    }

    public int OutputSampleRate => 0;
    public int OutputChannels => 0;
    public int OutputBitsPerSample => 0;
    public bool CanChangeFrameCount => false;

    public (int[] buffer, int frameCount) Apply(int[] buffer, int frameCount, int channels, int sampleRate, int bitsPerSample)
    {
        if (!_measured)
        {
            throw new InvalidOperationException(
                "LoudnessTransform.MeasureLoudness must be called with a true whole-file measurement before Apply -- " +
                "unlike PeakNormalizationTransform, EBU R128 integrated loudness cannot be safely estimated from a single decode block. " +
                "See AudioCutter.MeasureIntegratedLoudness(sourceFilePath).");
        }

        if (_measuredLufs is null or double.NegativeInfinity)
        {
            return (buffer, frameCount);
        }

        var gainDb = _targetLufs - _measuredLufs.Value;
        var gain = Math.Pow(10, gainDb / 20.0);
        if (Math.Abs(gain - 1.0) < 1e-10) return (buffer, frameCount);

        var (minValue, maxValue) = BitDepthFormatTransform.GetNativeRange(bitsPerSample);
        var n = frameCount * channels;
        for (var i = 0; i < n; i++)
            buffer[i] = (int)Math.Clamp((double)buffer[i] * gain, minValue, maxValue);

        return (buffer, frameCount);
    }
}
