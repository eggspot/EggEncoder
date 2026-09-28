namespace EggEncoder.Pcm;

/// <summary>
/// Applies linear gain to each sample. Gain of 1.0 = no-change; 0.5 = half; 2.0 = double.
/// Gain of 0 silences. All arithmetic is clamped to int range.
/// AOT-safe, managed, allocation-free (in-place mutation).
/// </summary>
public sealed class VolumeTransform : IPcmTransform
{
    private readonly double _gain;

    public VolumeTransform(double gain)
    {
        if (gain < 0) throw new ArgumentOutOfRangeException(nameof(gain), "Gain must be >= 0");
        _gain = gain;
    }

    /// <summary>Target gain factor (1.0 = identity).</summary>
    public double Gain => _gain;

    public int OutputSampleRate => 0;
    public int OutputChannels => 0;
    public int OutputBitsPerSample => 0;
    public bool CanChangeFrameCount => false;

    public (int[] buffer, int frameCount) Apply(int[] buffer, int frameCount, int channels, int sampleRate, int bitsPerSample)
    {
        if (_gain == 1.0) return (buffer, frameCount);

        var n = frameCount * channels;
        if (_gain == 0.0) { Array.Clear(buffer, 0, n); return (buffer, frameCount); }

        for (var i = 0; i < n; i++)
            buffer[i] = (int)Math.Clamp((double)buffer[i] * _gain, int.MinValue, int.MaxValue);

        return (buffer, frameCount);
    }
}

/// <summary>
/// Peak-normalizes audio to a target dBFS level.
/// Measures the peak |sample| value, then applies gain = targetPeakLinear * bitDepthMax / peak,
/// where bitDepthMax is the native maximum for whatever bit depth Apply() is currently seeing (see
/// <see cref="BitDepthFormatTransform"/>'s doc comment on this codebase's native-range convention) --
/// *not* a fixed 32-bit scale, since a source isn't necessarily 32-bit and gain computed against the
/// wrong scale would blow samples far outside their actual bit depth's range.
/// First implementation: peak normalization only (LUFS nice-to-have for future).
/// AOT-safe, managed. Measurement is O(n) and can be done in a pre-pass.
///
/// This transform measures from whatever buffer it's first given and then fixes that gain for
/// every later Apply() call -- it has no way to know it's only seeing part of a stream. Dropped
/// into a pipeline used with AudioCutter.Convert/Cut, which calls Apply() once per ~4096-frame
/// decode block, that means the gain is silently computed from the *first block's* peak, not the
/// whole file's. For a true whole-file measurement, decode the file yourself first (or use
/// AudioCutter.MeasurePeakAmplitude) and call <see cref="MeasurePeak(long)"/> with that value
/// before handing this transform to a pipeline.
/// </summary>
public sealed class PeakNormalizationTransform : IPcmTransform
{
    private readonly double _targetLin;
    private long? _peakAbsoluteSample;
    private bool _measured;

    /// <param name="targetDb">Target peak in dBFS, e.g. -1.0 (≈0.891 of the native range). Must be ≤ 0.</param>
    public PeakNormalizationTransform(double targetDb)
    {
        if (targetDb > 0) throw new ArgumentOutOfRangeException(nameof(targetDb), targetDb, "Target dBFS must be ≤ 0");
        _targetLin = Math.Pow(10, targetDb / 20.0);
    }

    /// <summary>
    /// Pre-measure from a single in-memory buffer before Apply is called. Only correct as a whole-file
    /// measurement if <paramref name="buffer"/> holds the entire stream, not just one decode block --
    /// for a block-by-block pipeline, decode the whole file first and use
    /// <see cref="MeasurePeak(long)"/> with the result instead.
    /// </summary>
    public void MeasurePeak(int[] buffer, int frameCount, int channels)
    {
        if (_measured) return;
        long maxAbs = 0;
        var n = frameCount * channels;
        for (var i = 0; i < n; i++)
        {
            var a = Math.Abs((long)buffer[i]);
            if (a > maxAbs) maxAbs = a;
        }
        MeasurePeak(maxAbs);
    }

    /// <summary>
    /// Fix the gain from an already-known peak absolute sample value (e.g. from
    /// AudioCutter.MeasurePeakAmplitude), without needing the samples themselves in memory.
    /// This is the correct way to get a true whole-file measurement into a block-by-block pipeline.
    /// </summary>
    public void MeasurePeak(long maxAbsoluteSample)
    {
        if (_measured) return;
        _peakAbsoluteSample = maxAbsoluteSample;
        _measured = true;
    }

    public int OutputSampleRate => 0;
    public int OutputChannels => 0;
    public int OutputBitsPerSample => 0;
    public bool CanChangeFrameCount => false;

    public (int[] buffer, int frameCount) Apply(int[] buffer, int frameCount, int channels, int sampleRate, int bitsPerSample)
    {
        if (!_measured) MeasurePeak(buffer, frameCount, channels);
        if (_peakAbsoluteSample is null or 0) return (buffer, frameCount);

        var (minValue, maxValue) = BitDepthFormatTransform.GetNativeRange(bitsPerSample);
        var gain = _targetLin * maxValue / _peakAbsoluteSample.Value;
        if (Math.Abs(gain - 1.0) < 1e-10) return (buffer, frameCount);

        var n = frameCount * channels;
        for (var i = 0; i < n; i++)
            buffer[i] = (int)Math.Clamp((double)buffer[i] * gain, minValue, maxValue);

        return (buffer, frameCount);
    }
}
