using EggEncoder.Codecs;

namespace EggEncoder.Pcm;

/// <summary>
/// Remixes channels: stereo→mono (avg L+R), mono→stereo (dup both), or arbitrary N→M up/downmix.
/// AOT-safe, managed implementation.
/// </summary>
public sealed class ChannelRemixTransform : IPcmTransform
{
    private readonly int _outputChannels;
    private readonly float[,]? _mixMatrix;

    /// <param name="inputChannels">Number of input channels (must match actual input).</param>
    /// <param name="outputChannels">Number of output channels requested.</param>
    /// <param name="mixMode">How to handle channel mapping (downmix, upmix, or auto).</param>
    public ChannelRemixTransform(int inputChannels, int outputChannels, ChannelRemixMode mixMode = ChannelRemixMode.Auto)
    {
        if (inputChannels <= 0 || outputChannels <= 0)
            throw new ArgumentOutOfRangeException("Channels must be positive");

        // Identity remix — passthrough, no mixing needed
        if (inputChannels == outputChannels && mixMode == ChannelRemixMode.Auto)
        {
            _outputChannels = inputChannels;
            _mixMatrix = null;
            return;
        }

        _outputChannels = outputChannels;
        _mixMatrix = BuildMixMatrix(inputChannels, outputChannels, mixMode);
    }

    public int OutputSampleRate => 0;   // passthrough — preserves input rate
    public int OutputChannels => _outputChannels;
    public int OutputBitsPerSample => 0;

    public (int[] buffer, int frameCount) Apply(int[] buffer, int frameCount, int channels, int sampleRate, int bitsPerSample)
    {
        // Identity passthrough
        if (_mixMatrix == null)
            return (buffer, frameCount);

        if (channels != _mixMatrix.GetLength(1))
            throw new ArgumentException($"ChannelRemixTransform expects {_mixMatrix.GetLength(1)} input channels but received {channels}", nameof(channels));

        var dstFrameCount = frameCount;
        var dstSampleCount = dstFrameCount * _outputChannels;
        var dstBuffer = new int[dstSampleCount];

        for (var dstFrame = 0; dstFrame < dstFrameCount; dstFrame++)
        {
            var dstBegin = dstFrame * _outputChannels;
            var srcBegin = dstFrame * channels;

            for (var outCh = 0; outCh < _outputChannels; outCh++)
            {
                var sum = 0.0;
                for (var inCh = 0; inCh < channels; inCh++)
                    sum += buffer[srcBegin + inCh] * _mixMatrix[outCh, inCh];

                dstBuffer[dstBegin + outCh] = (int)Math.Round(sum);
            }
        }

        return (dstBuffer, dstFrameCount);
    }

    private static float[,] BuildMixMatrix(int inCh, int outCh, ChannelRemixMode mode)
    {
        var matrix = new float[outCh, inCh];

        if (mode == ChannelRemixMode.PassThrough || (mode == ChannelRemixMode.Auto && inCh == outCh))
        {
            for (var i = 0; i < Math.Min(inCh, outCh); i++)
                matrix[i, i] = 1.0f;
            return matrix;
        }

        if (inCh == 2 && outCh == 1)
        {
            // Stereo → Mono: average L+R
            matrix[0, 0] = 0.5f;
            matrix[0, 1] = 0.5f;
        }
        else if (inCh == 1 && outCh == 2)
        {
            // Mono → Stereo: duplicate
            matrix[0, 0] = 1.0f;
            matrix[1, 0] = 1.0f;
        }
        else if (inCh > outCh)
        {
            // Downmix: equal-weight blend of input groups
            for (var outIdx = 0; outIdx < outCh; outIdx++)
            {
                var startIn = (int)Math.Floor((double)inCh * outIdx / outCh);
                var endIn = (int)Math.Ceiling((double)inCh * (outIdx + 1) / outCh);
                var count = endIn - startIn;
                var weight = 1.0f / count;
                for (var i = startIn; i < endIn; i++)
                    matrix[outIdx, i] = weight;
            }
        }
        else
        {
            // UpMix: duplicate channels cyclically
            for (var outIdx = 0; outIdx < outCh; outIdx++)
                matrix[outIdx, outIdx % inCh] = 1.0f;
        }

        return matrix;
    }
}

/// <summary>
/// How to handle channel count mapping when input and output channel counts differ.
/// </summary>
public enum ChannelRemixMode
{
    /// <summary>
    /// Auto-detect: identity when in==out, stereo→mono / mono→stereo / N→M otherwise.
    /// </summary>
    Auto,

    /// <summary>
    /// Pass through (identity mapping, only valid when inCh == outCh).
    /// </summary>
    PassThrough,

    /// <summary>
    /// Downmix: average channels to fewer outputs (equal-weight groups).
    /// </summary>
    Downmix,

    /// <summary>
    /// UpMix: duplicate channels to more outputs (cyclic repetition).
    /// </summary>
    UpMix
}
