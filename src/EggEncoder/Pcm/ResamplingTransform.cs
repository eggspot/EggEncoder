using EggEncoder.Codecs;

namespace EggEncoder.Pcm;

/// <summary>
/// Sample-rate conversion using linear interpolation. AOT-safe, managed implementation.
///
/// Choice: Linear interpolation for the first implementation. It is trivial, allocation-free
/// aside from the output buffer, and fully AOT-compatible with no external dependencies.
/// For production quality a Kaiser-windowed sinc or polyphase resampler would be preferable;
/// the interface is designed to swap implementations without changing callers.
///
/// Handles changing frame counts per block correctly: each block is resampled independently
/// using a fractional position tracker that advances across the full stream.
/// </summary>
public sealed class ResamplingTransform : IPcmTransform
{
    private readonly int _sourceRate;
    private readonly int _targetRate;
    private readonly int _channels;
    private readonly double _ratio; // output frames per input frame
    private double _streamPosition;  // fractional position in source frames, accumulated across blocks

    public ResamplingTransform(int sourceRate, int targetRate, int channels)
    {
        if (sourceRate <= 0 || targetRate <= 0) throw new ArgumentOutOfRangeException("Sample rates must be positive");
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels), "Channels must be positive");

        _sourceRate = sourceRate;
        _targetRate = targetRate;
        _channels = channels;
        _ratio = (double)targetRate / sourceRate;
    }

    public int OutputSampleRate => _targetRate;
    public int OutputChannels => _channels;
    public int OutputBitsPerSample => 0;

    /// <summary>Reset the cross-block fractional position tracker. Call at the start of a new stream.</summary>
    public void Reset()
    {
        _streamPosition = 0.0;
    }

    public (int[] buffer, int frameCount) Apply(int[] buffer, int frameCount, int channels, int sampleRate, int bitsPerSample)
    {
        if (channels != _channels)
            throw new ArgumentException($"Expected {_channels} channels but received {channels}", nameof(channels));
        if (sampleRate != _sourceRate)
            throw new ArgumentException($"Expected {_sourceRate} Hz but received {sampleRate} Hz", nameof(sampleRate));
        if (_ratio == 1.0) return (buffer, frameCount);

        var srcFrameCount = frameCount;
        var dstFrameCount = (int)Math.Max(1, Math.Round(srcFrameCount * _ratio));
        var dstBuffer = new int[dstFrameCount * _channels];

        var srcPos = _streamPosition;
        for (var dstFrame = 0; dstFrame < dstFrameCount; dstFrame++)
        {
            var srcIdx = (int)Math.Floor(srcPos);
            var srcFrac = srcPos - srcIdx;
            var dstBegin = dstFrame * _channels;

            if (srcIdx >= srcFrameCount - 1)
            {
                // Past last source frame: duplicate last sample
                for (var ch = 0; ch < _channels; ch++)
                    dstBuffer[dstBegin + ch] = buffer[(srcFrameCount - 1) * _channels + ch];
            }
            else if (srcIdx < 0)
            {
                for (var ch = 0; ch < _channels; ch++)
                    dstBuffer[dstBegin + ch] = buffer[ch];
            }
            else
            {
                var srcBegin = srcIdx * _channels;
                var srcNextBegin = (srcIdx + 1) * _channels;
                for (var ch = 0; ch < _channels; ch++)
                {
                    var v0 = buffer[srcBegin + ch];
                    var v1 = buffer[srcNextBegin + ch];
                    var interp = (int)Math.Round(v0 + (v1 - v0) * srcFrac);
                    dstBuffer[dstBegin + ch] = Math.Clamp(interp, int.MinValue, int.MaxValue);
                }
            }

            srcPos += 1.0 / _ratio;
        }

        _streamPosition = srcPos; // carry fractional position across blocks
        return (dstBuffer, dstFrameCount);
    }
}
