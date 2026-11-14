using EggEncoder.Waveform;

namespace EggEncoder.Pcm;

/// <summary>Loudness measurements of in-memory audio (ITU-R BS.1770-4 / EBU R128).</summary>
public static class LoudnessMeter
{
    /// <summary>
    /// Integrated loudness in LUFS: K-weighted, in 400 ms blocks with 75% overlap, gated at -70 LUFS and then 10 LU below
    /// the mean. Only complete 100 ms steps count, so audio shorter than 400 ms gives <see langword="null"/>; audio whose
    /// every block is quieter than -70 LUFS gives <see cref="double.NegativeInfinity"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="audio"/> is null.</exception>
    /// <exception cref="ArgumentException">The audio holds a NaN or infinite sample.</exception>
    public static double? MeasureIntegratedLoudness(PcmAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        audio.RequireFinite(nameof(audio));

        var accumulator = new IntegratedLoudnessAccumulator(audio.Channels, audio.SampleRate, countsPartialFinalStep: false);
        var samples = audio.Samples;
        for (var i = 0; i < samples.Length;)
        {
            for (var channel = 0; channel < audio.Channels; channel++, i++)
            {
                accumulator.AddSample(channel, samples[i]);
            }

            accumulator.EndFrame();
        }

        return accumulator.IntegratedLoudness();
    }

    /// <summary>
    /// True peak in dBTP: the largest absolute value of the samples and of three points interpolated between each pair of
    /// samples (4× oversampling with a 48-tap windowed-sinc filter; silence is assumed outside the audio).
    /// <see cref="double.NegativeInfinity"/> for silent or empty audio.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="audio"/> is null.</exception>
    /// <exception cref="ArgumentException">The audio holds a NaN or infinite sample.</exception>
    public static double MeasureTruePeakDb(PcmAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        audio.RequireFinite(nameof(audio));

        var peak = TruePeakOversampler.Peak(audio.Samples, audio.Channels);
        return peak > 0 ? 20.0 * Math.Log10(peak) : double.NegativeInfinity;
    }
}
