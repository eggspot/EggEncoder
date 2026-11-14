using EggEncoder.Codecs;
using EggEncoder.Codecs.Aac;
using EggEncoder.Containers.Mp4;
using EggEncoder.Media;
using EggEncoder.Transform;

namespace EggEncoder.Pcm;

/// <summary>
/// Mono or stereo audio held in memory as interleaved 32-bit float samples, nominally -1.0..1.0 and never clipped
/// (a mix can go above 1.0; <see cref="ToInt16"/> clips when converting). The input for <see cref="PcmMixer"/>,
/// <see cref="LoudnessMeter"/> and <see cref="LoudnessNormalizer"/>. Memory is
/// duration × sample rate × channels × 4 bytes (48 kHz stereo: 384 KB per second).
/// </summary>
public sealed class PcmAudio
{
    private const int Int16Max = short.MaxValue;

    /// <summary>Wraps <paramref name="samples"/> without copying it: later changes to the array show through.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="samples"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channels"/> or <paramref name="sampleRate"/> is not positive.</exception>
    /// <exception cref="NotSupportedException"><paramref name="channels"/> is more than 2.</exception>
    /// <exception cref="ArgumentException">The sample count is not a whole number of frames.</exception>
    public PcmAudio(float[] samples, int channels, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);
        RequireFormat(channels, sampleRate);
        if (samples.Length % channels != 0)
        {
            throw new ArgumentException($"{samples.Length} samples are not a whole number of {channels}-channel frames", nameof(samples));
        }

        Samples = samples;
        Channels = channels;
        SampleRate = sampleRate;
    }

    /// <summary>Interleaved samples, frame by frame.</summary>
    public float[] Samples { get; }

    /// <summary>1 or 2.</summary>
    public int Channels { get; }

    /// <summary>Samples per second per channel.</summary>
    public int SampleRate { get; }

    /// <summary>Samples per channel.</summary>
    public long FrameCount => Samples.Length / Channels;

    /// <summary>The length of the audio.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds(FrameCount / (double)SampleRate);

    /// <summary>
    /// Decodes the audio of a file. An MP4, M4A or MOV file gives its first AAC audio track, with the encoder's priming
    /// removed and the end trimmed as the file's edit list says (an empty edit at the start becomes leading silence);
    /// any other extension is decoded by the same decoders <c>AudioCutter</c> uses. Integer samples are divided by
    /// 2^(bits-1) - 1, the <see cref="FloatSampleConverter"/> scale, so 16-bit audio round-trips through <see cref="ToInt16"/>.
    /// </summary>
    /// <exception cref="NotSupportedException">The format, the MP4 audio codec (only AAC-LC), or a channel count above 2 is not supported.</exception>
    /// <exception cref="InvalidDataException">The file is damaged, has no audio track, or is too long to hold in one array.</exception>
    public static PcmAudio Decode(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".mp4" or ".m4a" or ".mov" ? DecodeMp4(path) : DecodeWithAudioCutter(path, extension);
    }

    /// <summary>Digital silence of the given length.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The duration is negative, or the format is invalid.</exception>
    /// <exception cref="NotSupportedException"><paramref name="channels"/> is more than 2.</exception>
    public static PcmAudio Silence(int channels, int sampleRate, TimeSpan duration)
    {
        RequireFormat(channels, sampleRate);
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        var frames = FramesOf(duration, sampleRate);
        return new PcmAudio(new float[CheckedLength(frames, channels)], channels, sampleRate);
    }

    /// <summary>
    /// The same audio at another sample rate and channel count. Stereo to mono averages the two channels, mono to stereo
    /// copies the channel, and the rate changes with a windowed-sinc filter; nothing is clipped. Returns this instance,
    /// sharing its array, when the format already matches.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The format is invalid.</exception>
    /// <exception cref="NotSupportedException"><paramref name="channels"/> is more than 2.</exception>
    public PcmAudio ConvertTo(int sampleRate, int channels)
    {
        RequireFormat(channels, sampleRate);
        if (sampleRate == SampleRate && channels == Channels)
        {
            return this;
        }

        var samples = Samples;
        if (channels < Channels)
        {
            samples = Downmix(samples);
        }

        samples = FloatResampler.Resample(samples, Math.Min(channels, Channels), SampleRate, sampleRate);
        if (channels > Channels)
        {
            samples = Upmix(samples);
        }

        return new PcmAudio(samples, channels, sampleRate);
    }

    /// <summary>Mono 16 kHz, the usual input of speech recognition models.</summary>
    public PcmAudio DownmixForSpeech() => ConvertTo(16000, 1);

    /// <summary>16-bit samples (× 32767, rounded and clipped; NaN becomes 0), for the AAC encoder or a WAV file.</summary>
    public short[] ToInt16()
    {
        var result = new short[Samples.Length];
        for (var i = 0; i < Samples.Length; i++)
        {
            var value = Samples[i];
            result[i] = float.IsNaN(value) ? (short)0 : (short)Math.Clamp(Math.Round(value * (double)Int16Max), short.MinValue, short.MaxValue);
        }

        return result;
    }

    internal void RequireFinite(string parameterName)
    {
        foreach (var sample in Samples)
        {
            if (!float.IsFinite(sample))
            {
                throw new ArgumentException("The audio holds a NaN or infinite sample", parameterName);
            }
        }
    }

    internal static void RequireFormat(int channels, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        if (channels > 2)
        {
            throw new NotSupportedException($"{channels}-channel audio is not supported; only mono and stereo are");
        }
    }

    internal static long FramesOf(TimeSpan duration, int sampleRate) => (long)Math.Round(duration.TotalSeconds * sampleRate);

    internal static int CheckedLength(long frames, int channels)
    {
        if (frames * channels > Array.MaxLength)
        {
            throw new ArgumentException("The audio is too long to hold in one array");
        }

        return (int)(frames * channels);
    }

    private static float[] Downmix(float[] stereo)
    {
        var mono = new float[stereo.Length / 2];
        for (var i = 0; i < mono.Length; i++)
        {
            mono[i] = (stereo[2 * i] + stereo[(2 * i) + 1]) / 2f;
        }

        return mono;
    }

    private static float[] Upmix(float[] mono)
    {
        var stereo = new float[mono.Length * 2];
        for (var i = 0; i < mono.Length; i++)
        {
            stereo[2 * i] = mono[i];
            stereo[(2 * i) + 1] = mono[i];
        }

        return stereo;
    }

    private static PcmAudio DecodeWithAudioCutter(string path, string extension)
    {
        var samples = new List<float>();
        var channels = 0;
        var sampleRate = 0;
        AudioCutter.DecodeSource(path, extension, (block, blockChannels, blockSampleRate, bitsPerSample, _) =>
        {
            if (channels == 0)
            {
                RequireFormat(blockChannels, blockSampleRate);
                channels = blockChannels;
                sampleRate = blockSampleRate;
            }

            var scale = 1.0 / ((1L << (bitsPerSample - 1)) - 1);
            if ((long)samples.Count + block.Length > Array.MaxLength)
            {
                throw new InvalidDataException("The audio is too long to hold in one array");
            }

            foreach (var sample in block)
            {
                samples.Add((float)(sample * scale));
            }
        });

        if (channels == 0)
        {
            throw new InvalidDataException($"'{path}' holds no audio");
        }

        return new PcmAudio([.. samples], channels, sampleRate);
    }

    private static PcmAudio DecodeMp4(string path)
    {
        using var source = Mp4FileSource.Open(path);
        var tracks = source.SourceTracks.Where(t => t.Info.Kind == MediaKind.Audio).ToList();
        if (tracks.Count == 0)
        {
            throw new InvalidDataException($"'{path}' has no audio track this library can read");
        }

        var track = tracks.FirstOrDefault(t => t.Info.Codec == MediaCodec.Aac)
            ?? throw new NotSupportedException($"The audio of '{path}' is {tracks[0].Info.Codec}; only AAC is decoded from MP4");
        var info = track.Info;
        RequireFormat(info.Channels, info.SampleRate);

        var endFrames = ToFrames(track.EditEnd ?? track.PresentationEnd(), info);
        var output = new float[CheckedLength(Math.Max(0, endFrames), info.Channels)];
        var decoder = new AacFrameDecoder(info.SampleRate, info.Channels);
        long? writeFrame = null;

        foreach (var packet in source.ReadTrackPackets(track))
        {
            int[] decoded;
            try
            {
                decoded = decoder.DecodeFrame(new BitReader(packet.Payload.ToArray()));
            }
            catch (EndOfStreamException e)
            {
                throw new InvalidDataException("AAC: an MP4 sample is truncated", e);
            }

            var frames = decoded.Length / info.Channels;
            var packetFrame = ToFrames(packet.Pts, info);
            writeFrame = writeFrame is long next && Math.Abs(packetFrame - next) <= frames ? next : packetFrame;
            Place(decoded, info.Channels, writeFrame.Value, output);
            writeFrame += frames;
        }

        return new PcmAudio(output, info.Channels, info.SampleRate);
    }

    private static long ToFrames(long ticks, MediaTrackInfo info) => (long)Math.Round(ticks * (double)info.SampleRate / info.Timescale);

    private static void Place(int[] decoded, int channels, long startFrame, float[] output)
    {
        for (var i = 0; i < decoded.Length; i++)
        {
            var index = (startFrame * channels) + i;
            if (index >= 0 && index < output.Length)
            {
                output[index] = decoded[i] / (float)Int16Max;
            }
        }
    }
}
