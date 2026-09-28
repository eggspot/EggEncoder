namespace EggEncoder.Pcm;

/// <summary>
/// A single PCM transform that operates on interleaved int[] buffers between decode and encode.
/// Transforms may mutate the buffer in-place or replace it; the pipeline passes the result to the next transform.
/// </summary>
public interface IPcmTransform
{
    int OutputSampleRate { get; }
    int OutputChannels { get; }

    /// <summary>Output bit depth in bits, or 0 if this transform preserves whatever bit depth it receives.</summary>
    int OutputBitsPerSample { get; }

    /// <summary>
    /// True if this transform can return a different frame count than it was given (e.g. resampling).
    /// Lets a caller that already knows the exact input frame count (like AudioCutter.Cut, after
    /// trimming) decide whether it can still predict the exact output frame count up front, or must
    /// wait to see actual output before it can open a sink that needs one (see DeferredWavSink).
    /// </summary>
    bool CanChangeFrameCount { get; }

    (int[] buffer, int frameCount) Apply(int[] buffer, int frameCount, int channels, int sampleRate, int bitsPerSample);
}

/// <summary>
/// A pipeline of PCM transforms applied between decode and encode.
/// Transforms are applied in order; each receives the output of the previous.
/// Default (empty) pipeline is a no-op passthrough for backward compatibility.
/// </summary>
public sealed class PcmTransformPipeline
{
    private readonly IPcmTransform[] _transforms;

    public PcmTransformPipeline(params IPcmTransform[] transforms)
    {
        _transforms = transforms ?? [];
    }

    public bool HasTransforms => _transforms.Length > 0;
    public IReadOnlyList<IPcmTransform> Transforms => _transforms;

    /// <summary>True if any transform in this pipeline can change frame count (see <see cref="IPcmTransform.CanChangeFrameCount"/>).</summary>
    public bool CanChangeFrameCount
    {
        get
        {
            foreach (var transform in _transforms)
            {
                if (transform.CanChangeFrameCount) return true;
            }

            return false;
        }
    }

    public (int[] buffer, int frameCount, int channels, int sampleRate, int bitsPerSample) Apply(
        int[] inputBuffer, int frameCount, int channels, int sampleRate, int bitsPerSample)
    {
        var buffer = inputBuffer;
        var fCount = frameCount;
        var ch = channels;
        var sr = sampleRate;
        var bps = bitsPerSample;

        foreach (var transform in _transforms)
        {
            var result = transform.Apply(buffer, fCount, ch, sr, bps);
            buffer = result.buffer;
            fCount = result.frameCount;
            ApplyFormatOverride(transform, ref ch, ref sr, ref bps);
        }

        return (buffer, fCount, ch, sr, bps);
    }

    /// <summary>
    /// Determines the (channels, sampleRate, bitsPerSample) a sink should be opened with, without
    /// processing any audio. Mirrors the format-tracking side of <see cref="Apply"/> exactly (both
    /// walk the same transform list applying the same per-transform override rule, so they cannot
    /// drift apart), letting a caller open its destination sink before the first block arrives.
    /// </summary>
    public (int channels, int sampleRate, int bitsPerSample) ComputeOutputFormat(int inputChannels, int inputSampleRate, int inputBitsPerSample)
    {
        var ch = inputChannels;
        var sr = inputSampleRate;
        var bps = inputBitsPerSample;

        foreach (var transform in _transforms)
        {
            ApplyFormatOverride(transform, ref ch, ref sr, ref bps);
        }

        return (ch, sr, bps);
    }

    private static void ApplyFormatOverride(IPcmTransform transform, ref int channels, ref int sampleRate, ref int bitsPerSample)
    {
        if (transform.OutputChannels != 0) channels = transform.OutputChannels;
        if (transform.OutputSampleRate != 0) sampleRate = transform.OutputSampleRate;
        if (transform.OutputBitsPerSample != 0) bitsPerSample = transform.OutputBitsPerSample;
    }
}

/// <summary>No-op transform for passthrough.</summary>
public sealed class NoOpTransform : IPcmTransform
{
    public static readonly NoOpTransform Instance = new();
    public int OutputSampleRate => 0;
    public int OutputChannels => 0;
    public int OutputBitsPerSample => 0;
    public bool CanChangeFrameCount => false;
    public (int[] buffer, int frameCount) Apply(int[] buffer, int frameCount, int channels, int sampleRate, int bitsPerSample) => (buffer, frameCount);
}
