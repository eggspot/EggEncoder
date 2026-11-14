namespace EggEncoder.Pcm;

/// <summary>
/// Mixes several inputs into one by summing them, each with its own gain, start offset and looping, the way ffmpeg's
/// <c>amix normalize=0</c> with <c>adelay</c> and <c>aloop</c> does: no normalization and no clipping, so the result can go
/// above 1.0 (normalize it with <see cref="LoudnessNormalizer"/>, or let <see cref="PcmAudio.ToInt16"/> clip it).
/// </summary>
public static class PcmMixer
{
    /// <summary>
    /// Mixes <paramref name="inputs"/> into new audio of the given format. Each input ends at its start offset plus its
    /// length; a looping input never ends. The mix runs for <paramref name="fixedLength"/> when it is given (looping inputs
    /// are then fine with any rule), otherwise as <paramref name="lengthRule"/> says.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="inputs"/> or an input's audio is null.</exception>
    /// <exception cref="ArgumentException">There are no inputs, or the length rule meets a looping input and so never ends.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A gain is not finite, an offset or the fixed length is negative, or the format is invalid.</exception>
    /// <exception cref="NotSupportedException"><paramref name="channels"/> is more than 2.</exception>
    public static PcmAudio Mix(
        IReadOnlyList<PcmMixInput> inputs,
        PcmMixLength lengthRule = PcmMixLength.Longest,
        TimeSpan? fixedLength = null,
        int sampleRate = 48000,
        int channels = 2)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        PcmAudio.RequireFormat(channels, sampleRate);
        if (inputs.Count == 0)
        {
            throw new ArgumentException("A mix needs at least one input", nameof(inputs));
        }

        if (fixedLength is { } fixedDuration)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(fixedDuration, TimeSpan.Zero, nameof(fixedLength));
        }

        var tracks = new List<MixTrack>(inputs.Count);
        foreach (var input in inputs)
        {
            ArgumentNullException.ThrowIfNull(input.Audio, nameof(inputs));
            if (!double.IsFinite(input.Gain))
            {
                throw new ArgumentOutOfRangeException(nameof(inputs), input.Gain, "A gain must be a finite number");
            }

            if (input.StartOffset < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(inputs), input.StartOffset, "A start offset must not be negative");
            }

            var audio = input.Audio.ConvertTo(sampleRate, channels);
            tracks.Add(new MixTrack(audio.Samples, input.Gain, PcmAudio.FramesOf(input.StartOffset, sampleRate), input.Loop && audio.Samples.Length > 0));
        }

        var totalFrames = fixedLength is { } length ? PcmAudio.FramesOf(length, sampleRate) : RuleLength(tracks, lengthRule, channels);
        var output = new float[PcmAudio.CheckedLength(totalFrames, channels)];
        foreach (var track in tracks)
        {
            Add(track, channels, totalFrames, output);
        }

        return new PcmAudio(output, channels, sampleRate);
    }

    private static long RuleLength(List<MixTrack> tracks, PcmMixLength rule, int channels)
    {
        long? End(MixTrack track) => track.Loop ? null : track.OffsetFrames + (track.Samples.Length / channels);

        var ends = tracks.Select(End).ToList();
        return rule switch
        {
            PcmMixLength.First => ends[0] ?? throw new ArgumentException("The first input loops, so a mix that ends with it never ends; give a fixed length"),
            PcmMixLength.Shortest => ends.Min() ?? throw new ArgumentException("Every input loops, so the mix never ends; give a fixed length"),
            PcmMixLength.Longest => ends.Contains(null)
                ? throw new ArgumentException("An input loops, so a mix that ends with the longest input never ends; give a fixed length")
                : ends.Max()!.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown mix length rule")
        };
    }

    private static void Add(MixTrack track, int channels, long totalFrames, float[] output)
    {
        var frames = track.Samples.Length / channels;
        if (frames == 0)
        {
            return;
        }

        var lastFrame = track.Loop ? totalFrames : Math.Min(totalFrames, track.OffsetFrames + frames);
        var gain = (float)track.Gain;
        for (var frame = track.OffsetFrames; frame < lastFrame; frame++)
        {
            var source = (frame - track.OffsetFrames) % frames * channels;
            var target = frame * channels;
            for (var channel = 0; channel < channels; channel++)
            {
                output[target + channel] += gain * track.Samples[source + channel];
            }
        }
    }

    private readonly record struct MixTrack(float[] Samples, double Gain, long OffsetFrames, bool Loop);
}
