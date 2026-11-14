namespace EggEncoder.Pcm;

/// <summary>
/// Brings audio to a target integrated loudness without its true peak passing a ceiling: one gain from the measured
/// loudness, then a true-peak limiter with a 5 ms lookahead and a 100 ms release. Close to ffmpeg's two-pass
/// <c>loudnorm</c> in linear mode, not identical: when the limiter has to work, the result can come out quieter than the
/// target. The whole audio is held in memory: the input, the output and per-frame working arrays, about
/// 2 MB per second of 48 kHz stereo.
/// </summary>
public static class LoudnessNormalizer
{
    /// <summary>The ceiling used when none is given, in dBTP.</summary>
    public const double DefaultTruePeakCeilingDb = -1.5;

    /// <summary>
    /// A normalized copy of <paramref name="audio"/> (the input is not changed). Audio too short to measure (under 400 ms)
    /// or measured as silence gets no gain and is only limited to the ceiling.
    /// </summary>
    /// <param name="audio">The audio to normalize.</param>
    /// <param name="targetLufs">Target integrated loudness, from -70 to 0 LUFS (e.g. -16 or -14 for streaming).</param>
    /// <param name="truePeakCeilingDb">The highest true peak allowed in the result, in dBTP, at most 0.</param>
    /// <exception cref="ArgumentNullException"><paramref name="audio"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The target or the ceiling is out of range.</exception>
    /// <exception cref="ArgumentException">The audio holds a NaN or infinite sample.</exception>
    public static PcmAudio Normalize(PcmAudio audio, double targetLufs, double truePeakCeilingDb = DefaultTruePeakCeilingDb)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (!double.IsFinite(targetLufs) || targetLufs is < -70.0 or > 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetLufs), targetLufs, "The target must be from -70 to 0 LUFS");
        }

        if (!double.IsFinite(truePeakCeilingDb) || truePeakCeilingDb > 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(truePeakCeilingDb), truePeakCeilingDb, "The ceiling must be a finite number at most 0 dBTP");
        }

        var measured = LoudnessMeter.MeasureIntegratedLoudness(audio);
        var samples = (float[])audio.Samples.Clone();
        if (measured is double loudness && double.IsFinite(loudness))
        {
            var gain = (float)Math.Pow(10.0, (targetLufs - loudness) / 20.0);
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] *= gain;
            }
        }

        TruePeakLimiter.Apply(samples, audio.Channels, audio.SampleRate, Math.Pow(10.0, truePeakCeilingDb / 20.0));
        return new PcmAudio(samples, audio.Channels, audio.SampleRate);
    }
}
