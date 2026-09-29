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

        // Clamp to the *native* range for the current bit depth, not the full int32 range: a value
        // that overflows its bit depth but still fits in int32 (e.g. 60000 from a 16-bit sample after
        // gain) would otherwise reach the sink unclamped and get truncated on write (WavWriter's 16-bit
        // path just takes the low two bytes), producing wraparound distortion instead of a clean clip.
        var (minValue, maxValue) = BitDepthFormatTransform.GetNativeRange(bitsPerSample);
        for (var i = 0; i < n; i++)
            buffer[i] = (int)Math.Clamp((double)buffer[i] * _gain, minValue, maxValue);

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
/// AudioCutter.MeasurePeakAmplitude) and call <see cref="MeasurePeak(long, int)"/> with that value
/// before handing this transform to a pipeline.
/// </summary>
public sealed class PeakNormalizationTransform : IPcmTransform
{
    private readonly double _targetLin;
    private long? _peakAbsoluteSample;
    private int _peakBitsPerSample;
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
    /// <see cref="MeasurePeak(long, int)"/> with the result instead.
    /// </summary>
    /// <param name="buffer">Interleaved samples to scan.</param>
    /// <param name="frameCount">Number of frames in <paramref name="buffer"/>.</param>
    /// <param name="channels">Number of interleaved channels in <paramref name="buffer"/>.</param>
    /// <param name="bitsPerSample">The bit depth <paramref name="buffer"/>'s values are scaled to.</param>
    public void MeasurePeak(int[] buffer, int frameCount, int channels, int bitsPerSample)
    {
        if (_measured) return;
        MeasurePeak(ComputeMaxAbsoluteSample(buffer.AsSpan(0, frameCount * channels)), bitsPerSample);
    }

    /// <summary>Scans a buffer of interleaved samples for the largest absolute value. Shared by this
    /// type's own in-memory measurement and by AudioCutter.MeasurePeakAmplitude's decode-and-scan.</summary>
    public static long ComputeMaxAbsoluteSample(ReadOnlySpan<int> samples)
    {
        long maxAbs = 0;
        foreach (var sample in samples)
        {
            var a = Math.Abs((long)sample);
            if (a > maxAbs) maxAbs = a;
        }
        return maxAbs;
    }

    /// <summary>
    /// Fix the gain from an already-known peak absolute sample value (e.g. from
    /// AudioCutter.MeasurePeakAmplitude), without needing the samples themselves in memory.
    /// This is the correct way to get a true whole-file measurement into a block-by-block pipeline.
    /// </summary>
    /// <param name="maxAbsoluteSample">The peak absolute sample value.</param>
    /// <param name="bitsPerSample">
    /// The bit depth <paramref name="maxAbsoluteSample"/> is scaled to (normally the source file's own
    /// bit depth, e.g. from AudioCutter.MeasurePeakAmplitude). If a <see cref="BitDepthFormatTransform"/>
    /// runs earlier in the same pipeline, Apply() will see a different bitsPerSample than this one --
    /// the measured peak is automatically rescaled to match, so gain stays correct either way.
    /// </param>
    public void MeasurePeak(long maxAbsoluteSample, int bitsPerSample)
    {
        if (_measured) return;
        _peakAbsoluteSample = maxAbsoluteSample;
        _peakBitsPerSample = bitsPerSample;
        _measured = true;
    }

    public int OutputSampleRate => 0;
    public int OutputChannels => 0;
    public int OutputBitsPerSample => 0;
    public bool CanChangeFrameCount => false;

    public (int[] buffer, int frameCount) Apply(int[] buffer, int frameCount, int channels, int sampleRate, int bitsPerSample)
    {
        if (!_measured) MeasurePeak(buffer, frameCount, channels, bitsPerSample);
        if (_peakAbsoluteSample is null or 0) return (buffer, frameCount);

        var peak = _peakAbsoluteSample.Value;
        if (_peakBitsPerSample != bitsPerSample)
        {
            // The peak was measured at a different bit depth than Apply is currently seeing (e.g. a
            // BitDepthFormatTransform ran between measurement and here) -- rescale it to match, the
            // same way BitDepthFormatTransform itself would rescale the samples.
            var (_, fromMax) = BitDepthFormatTransform.GetNativeRange(_peakBitsPerSample);
            var (_, toMax) = BitDepthFormatTransform.GetNativeRange(bitsPerSample);
            peak = (long)Math.Round((double)peak * toMax / fromMax);
        }

        var (minValue, maxValue) = BitDepthFormatTransform.GetNativeRange(bitsPerSample);
        var gain = _targetLin * maxValue / peak;
        if (Math.Abs(gain - 1.0) < 1e-10) return (buffer, frameCount);

        var n = frameCount * channels;
        for (var i = 0; i < n; i++)
            buffer[i] = (int)Math.Clamp((double)buffer[i] * gain, minValue, maxValue);

        return (buffer, frameCount);
    }
}
