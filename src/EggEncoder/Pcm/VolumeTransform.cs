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
/// Measures the peak |sample| value, then applies gain = targetPeakLinear * int.MaxValue / peak.
/// First implementation: peak normalization only (LUFS nice-to-have for future).
/// AOT-safe, managed. Measurement is O(n) and can be done in a pre-pass.
/// </summary>
public sealed class PeakNormalizationTransform : IPcmTransform
{
    private readonly double _targetDb;
    private double? _gain;
    private bool _measured;

    /// <param name="targetDb">Target peak in dBFS, e.g. -1.0 (≈0.891 normalized peak). Must be ≤ 0.</param>
    public PeakNormalizationTransform(double targetDb)
    {
        if (targetDb > 0) throw new ArgumentOutOfRangeException(nameof(targetDb), "Target dBFS must be ≤ 0");
        _targetDb = targetDb;
    }

    /// <summary>Optionally pre-measure a source buffer before Apply is called.</summary>
    public void MeasurePeak(int[] buffer, int frameCount, int channels)
    {
        if (_measured) return;
        var maxAbs = 0;
        var n = frameCount * channels;
        for (var i = 0; i < n; i++)
        {
            var a = Math.Abs(buffer[i]);
            if (a > maxAbs) maxAbs = a;
        }
        if (maxAbs == 0) { _gain = 1.0; }
        else
        {
            var targetLin = Math.Pow(10, _targetDb / 20.0);
            _gain = targetLin * int.MaxValue / maxAbs;
        }
        _measured = true;
    }

    public int OutputSampleRate => 0;
    public int OutputChannels => 0;
    public int OutputBitsPerSample => 0;

    public (int[] buffer, int frameCount) Apply(int[] buffer, int frameCount, int channels, int sampleRate, int bitsPerSample)
    {
        if (!_measured) MeasurePeak(buffer, frameCount, channels);
        if (_gain == null || Math.Abs(_gain.Value - 1.0) < 1e-10) return (buffer, frameCount);

        var n = frameCount * channels;
        for (var i = 0; i < n; i++)
            buffer[i] = (int)Math.Clamp((double)buffer[i] * _gain.Value, int.MinValue, int.MaxValue);

        return (buffer, frameCount);
    }
}
