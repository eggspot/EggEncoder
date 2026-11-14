namespace EggEncoder.Pcm;

/// <summary>
/// Lowers a target signal while a key signal is loud: music ducked under a voice, the way ffmpeg's
/// <c>sidechaincompress</c> filter (downward mode) does, with the same parameters and the same attack and release
/// timing (a smoothing coefficient of min(1, 4000 / (time in ms × sample rate)) per sample), a soft knee around the
/// threshold, and gain worked out in the log domain. Two inputs, so it is not an <see cref="IPcmTransform"/>: feed
/// it the key and the target block by block with <see cref="Process"/>, or a whole clip with <see cref="Duck"/>.
/// Works on float samples (-1.0..1.0 nominal) and keeps its smoothed key level across calls.
/// </summary>
public sealed class SidechainCompressor
{
    private readonly int _keyChannels;
    private readonly int _targetChannels;
    private readonly SidechainCompressorSettings _settings;
    private readonly double _attackCoefficient;
    private readonly double _releaseCoefficient;
    private readonly double _levelAboveWhichGainApplies;
    private readonly double _logThreshold;
    private readonly double _logKneeStart;
    private readonly double _logKneeStop;
    private readonly double _compressedLogKneeStop;
    private readonly double _slopeScale;

    private double _level;

    /// <param name="sampleRate">Sample rate of both inputs, in Hz.</param>
    /// <param name="keyChannels">Channels of the key, 1 or 2.</param>
    /// <param name="targetChannels">Channels of the target, 1 or 2.</param>
    /// <param name="settings">The compressor's settings.</param>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The sample rate is not positive, a channel count is not 1 or 2, or a setting is out of range.</exception>
    public SidechainCompressor(int sampleRate, int keyChannels, int targetChannels, SidechainCompressorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        RequireChannels(keyChannels, nameof(keyChannels));
        RequireChannels(targetChannels, nameof(targetChannels));
        settings.Validate();

        _keyChannels = keyChannels;
        _targetChannels = targetChannels;
        _settings = settings;
        _attackCoefficient = Math.Min(1.0, 4000.0 / (settings.AttackMs * sampleRate));
        _releaseCoefficient = Math.Min(1.0, 4000.0 / (settings.ReleaseMs * sampleRate));

        var rms = settings.Detection == SidechainDetection.Rms;
        var kneeStart = settings.Threshold / Math.Sqrt(settings.Knee);
        var kneeStop = settings.Threshold * Math.Sqrt(settings.Knee);
        _levelAboveWhichGainApplies = rms ? kneeStart * kneeStart : kneeStart;
        _slopeScale = rms ? 0.5 : 1.0;
        _logThreshold = Math.Log(settings.Threshold);
        _logKneeStart = Math.Log(kneeStart);
        _logKneeStop = Math.Log(kneeStop);
        _compressedLogKneeStop = Compressed(_logKneeStop);
    }

    internal double SmoothedKeyLevel => _level;

    /// <summary>Forgets the smoothed key level, as if the key had always been silent.</summary>
    public void Reset() => _level = 0;

    /// <summary>
    /// Reduces <paramref name="target"/> in place according to <paramref name="key"/>. Both hold interleaved samples
    /// for the same number of frames, in the channel counts given at construction.
    /// </summary>
    /// <exception cref="ArgumentException">A span is not a whole number of frames, or the two hold different frame counts.</exception>
    public void Process(ReadOnlySpan<float> key, Span<float> target)
    {
        if (key.Length % _keyChannels != 0 || target.Length % _targetChannels != 0)
        {
            throw new ArgumentException("The key or the target is not a whole number of frames");
        }

        var frames = key.Length / _keyChannels;
        if (target.Length / _targetChannels != frames)
        {
            throw new ArgumentException($"The key holds {frames} frames but the target holds {target.Length / _targetChannels}");
        }

        for (var frame = 0; frame < frames; frame++)
        {
            var gain = GainFor(KeyLevel(key.Slice(frame * _keyChannels, _keyChannels)));
            ApplyGain(target.Slice(frame * _targetChannels, _targetChannels), gain);
        }
    }

    /// <summary>
    /// The target ducked by the key, as new audio in the target's format. The key is converted to the target's sample
    /// rate. A longer key is cut; a shorter one counts as silence after its end, so the output always has the
    /// target's length (ffmpeg ends its output with the key instead).
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A setting is out of range.</exception>
    public static PcmAudio Duck(PcmAudio target, PcmAudio key, SidechainCompressorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(settings);

        var convertedKey = key.ConvertTo(key.Channels, target.SampleRate);
        var compressor = new SidechainCompressor(target.SampleRate, key.Channels, target.Channels, settings);
        var output = (float[])target.Samples.Clone();
        var keyFrames = (int)Math.Min(convertedKey.FrameCount, target.FrameCount);

        compressor.Process(convertedKey.Samples.AsSpan(0, keyFrames * key.Channels), output.AsSpan(0, keyFrames * target.Channels));
        compressor.ProcessSilentKey(output.AsSpan(keyFrames * target.Channels));
        return new PcmAudio(output, target.Channels, target.SampleRate);
    }

    private void ProcessSilentKey(Span<float> target)
    {
        for (var start = 0; start < target.Length; start += _targetChannels)
        {
            ApplyGain(target.Slice(start, _targetChannels), GainFor(0));
        }
    }

    private double KeyLevel(ReadOnlySpan<float> keyFrame)
    {
        double level;
        if (_keyChannels == 1)
        {
            level = Math.Abs(keyFrame[0]);
        }
        else if (_settings.Link == SidechainLink.Maximum)
        {
            level = Math.Max(Math.Abs(keyFrame[0]), Math.Abs(keyFrame[1]));
        }
        else
        {
            level = (Math.Abs(keyFrame[0]) + Math.Abs((double)keyFrame[1])) / 2;
        }

        level *= _settings.KeyGain;
        return _settings.Detection == SidechainDetection.Rms ? level * level : level;
    }

    private double GainFor(double keyLevel)
    {
        _level += (keyLevel - _level) * (keyLevel > _level ? _attackCoefficient : _releaseCoefficient);
        if (_level <= 0 || _level <= _levelAboveWhichGainApplies)
        {
            return 1;
        }

        var slope = Math.Log(_level) * _slopeScale;
        var output = Compressed(slope);
        if (_settings.Knee > 1 && slope < _logKneeStop)
        {
            output = Hermite(slope, _logKneeStart, _logKneeStop, _logKneeStart, _compressedLogKneeStop, 1, 1 / _settings.Ratio);
        }

        return Math.Exp(output - slope);
    }

    private double Compressed(double logLevel) => ((logLevel - _logThreshold) / _settings.Ratio) + _logThreshold;

    private void ApplyGain(Span<float> targetFrame, double gain)
    {
        var scale = _settings.InputGain * ((gain * _settings.Makeup * _settings.Mix) + (1 - _settings.Mix));
        for (var channel = 0; channel < targetFrame.Length; channel++)
        {
            targetFrame[channel] = (float)(targetFrame[channel] * scale);
        }
    }

    private static double Hermite(double x, double x0, double x1, double y0, double y1, double slope0, double slope1)
    {
        var width = x1 - x0;
        var t = (x - x0) / width;
        var m0 = slope0 * width;
        var m1 = slope1 * width;
        var c2 = (-3 * y0) - (2 * m0) + (3 * y1) - m1;
        var c3 = (2 * y0) + m0 - (2 * y1) + m1;
        return (((((c3 * t) + c2) * t) + m0) * t) + y0;
    }

    private static void RequireChannels(int channels, string name)
    {
        if (channels is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(name, channels, "Channels must be 1 or 2");
        }
    }
}
