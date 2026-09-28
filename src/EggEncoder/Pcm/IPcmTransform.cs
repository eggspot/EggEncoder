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
            if (transform.OutputChannels != 0) ch = transform.OutputChannels;
            if (transform.OutputSampleRate != 0) sr = transform.OutputSampleRate;
            if (transform.OutputBitsPerSample != 0) bps = transform.OutputBitsPerSample;
        }

        return (buffer, fCount, ch, sr, bps);
    }

    /// <summary>
    /// Determines the (channels, sampleRate, bitsPerSample) a sink should be opened with, without
    /// processing any audio. Mirrors the format-tracking side of <see cref="Apply"/> exactly, so a
    /// caller can open its destination sink before the first block arrives.
    /// </summary>
    public (int channels, int sampleRate, int bitsPerSample) ComputeOutputFormat(int inputChannels, int inputSampleRate, int inputBitsPerSample)
    {
        var ch = inputChannels;
        var sr = inputSampleRate;
        var bps = inputBitsPerSample;

        foreach (var transform in _transforms)
        {
            if (transform.OutputChannels != 0) ch = transform.OutputChannels;
            if (transform.OutputSampleRate != 0) sr = transform.OutputSampleRate;
            if (transform.OutputBitsPerSample != 0) bps = transform.OutputBitsPerSample;
        }

        return (ch, sr, bps);
    }
}

/// <summary>No-op transform for passthrough.</summary>
public sealed class NoOpTransform : IPcmTransform
{
    public static readonly NoOpTransform Instance = new();
    public int OutputSampleRate => 0;
    public int OutputChannels => 0;
    public int OutputBitsPerSample => 0;
    public (int[] buffer, int frameCount) Apply(int[] buffer, int frameCount, int channels, int sampleRate, int bitsPerSample) => (buffer, frameCount);
}
