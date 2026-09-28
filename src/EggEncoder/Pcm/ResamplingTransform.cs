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
/// using a fractional position tracker that advances across the full stream, so the total output
/// frame count across many blocks matches processing the whole stream as one block.
///
/// Known limitation: a destination frame whose interpolation would need the *next* block's first
/// source sample (i.e. one landing on the last source frame of the current block with a nonzero
/// fractional offset) duplicates that last sample instead, since the next block hasn't been
/// decoded yet when the current one is processed. This affects at most one interpolated frame per
/// block boundary -- with the default 4096-frame decode block size that's a negligible fraction of
/// output, but it means block-by-block output is not bit-for-bit identical to resampling the same
/// audio as a single block. Fixing it would require buffering the last pending output frame across
/// Apply() calls (and a final flush once the stream ends), which the current IPcmTransform contract
/// (no end-of-stream hook) doesn't support.
///
/// Each block's own output frame count is round(thisBlock'sFrameCount * ratio), computed
/// independently per call rather than from a running cumulative total -- so for a stream split into
/// many small or unevenly-sized blocks, per-block rounding can drift the total by a frame or so from
/// what a single whole-stream pass would produce (in exchange for not needing to know the whole
/// stream's length up front). With the default 4096-frame decode block size this drift is negligible.
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
        if (sourceRate <= 0) throw new ArgumentOutOfRangeException(nameof(sourceRate), sourceRate, "Sample rates must be positive");
        if (targetRate <= 0) throw new ArgumentOutOfRangeException(nameof(targetRate), targetRate, "Sample rates must be positive");
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channels must be positive");

        _sourceRate = sourceRate;
        _targetRate = targetRate;
        _channels = channels;
        _ratio = (double)targetRate / sourceRate;
    }

    public int OutputSampleRate => _targetRate;
    public int OutputChannels => _channels;
    public int OutputBitsPerSample => 0;
    public bool CanChangeFrameCount => _ratio != 1.0;

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
        if (_ratio == 1.0 || frameCount <= 0) return (buffer, frameCount);

        var srcFrameCount = frameCount;
        var dstFrameCount = (int)Math.Round(srcFrameCount * _ratio);
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
                    // Widen to double before subtracting: v1 - v0 as int can overflow int32 for
                    // widely-separated 32-bit-depth samples (e.g. near int.MinValue and int.MaxValue).
                    double v0 = buffer[srcBegin + ch];
                    double v1 = buffer[srcNextBegin + ch];
                    var interp = v0 + ((v1 - v0) * srcFrac);
                    dstBuffer[dstBegin + ch] = (int)Math.Clamp(Math.Round(interp), int.MinValue, int.MaxValue);
                }
            }

            srcPos += 1.0 / _ratio;
        }

        // Rebase relative to the next block's frame indexing: the next Apply() call gets a fresh,
        // zero-based buffer, so a position expressed in terms of *this* block's indices (e.g. 3.0
        // after a 3-frame block) must become "0.0 into the next block", not carry forward unchanged
        // (which would immediately overflow the new block's valid index range on the very first
        // destination frame -- collapsing every subsequent block to a duplicated flat sample).
        _streamPosition = srcPos - srcFrameCount;
        return (dstBuffer, dstFrameCount);
    }
}
