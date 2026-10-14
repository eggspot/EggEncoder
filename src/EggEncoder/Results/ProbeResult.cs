namespace EggEncoder.Results
{
    public class ProbeResult
    {
        public required string FormatName { get; init; }

        public required string FormatLongName { get; init; }

        public required long SizeBytes { get; init; }

        public required double DurationSeconds { get; init; }

        public required string CodecType { get; init; }

        public string? CodecName { get; init; }

        public string? CodecLongName { get; init; }

        public int? SampleRate { get; init; }

        public int? Channels { get; init; }

        public string? ChannelLayout { get; init; }

        public int? BitsPerSample { get; init; }

        public int? BitRate { get; init; }

        public bool? IsVariableBitRate { get; init; }

        public long? DurationInSamples { get; init; }

        public string? TimeBase { get; init; }

        public int? Width { get; init; }

        public int? Height { get; init; }

        public IReadOnlyList<double>? Waveform { get; init; }

        /// <summary>
        /// The single loudest absolute sample value across the whole decoded audio, normalized to
        /// 0.0..1.0 (1.0 = the bit depth's own full-scale maximum). Null for a file this probe
        /// couldn't decode audio from (e.g. MOV/MP4 with no matching audio track).
        /// </summary>
        public double? PeakAmplitude { get; init; }

        /// <summary>
        /// Root-mean-square level across every individual sample of the whole decoded audio,
        /// normalized the same way <see cref="PeakAmplitude"/> is. A rough loudness indicator --
        /// not a perceptual/ITU-R BS.1770 loudness measurement. Null for a file this probe
        /// couldn't decode audio from.
        /// </summary>
        public double? RmsLevel { get; init; }

        /// <summary>
        /// Integrated loudness in LUFS, per ITU-R BS.1770-4 / EBU R128 -- the real perceptual
        /// loudness measurement <see cref="RmsLevel"/>'s own doc comment says it is not. Null for
        /// a file this probe couldn't decode audio from, or for audio shorter than the standard's
        /// own minimum 400ms gating block. Can legitimately be <see cref="double.NegativeInfinity"/>
        /// for audio that's entirely at or below the standard's -70 LUFS absolute silence gate --
        /// that's a real measurement, not a sentinel for "couldn't measure" (which is what the
        /// null case means instead).
        /// </summary>
        public double? IntegratedLoudness { get; init; }

        /// <summary>
        /// Metadata tags, keyed by their raw format-native id exactly as the container stores them
        /// (e.g. "INAM" for WAV's own title chunk, "TIT2" for MP3's own ID3v2 title frame) -- not
        /// remapped to friendlier names, the same way <see cref="CodecName"/> exposes "pcm_s16le"
        /// rather than a human label. Populated for WAV files with a 'LIST'/'INFO' chunk, and for
        /// MP3 files with an ID3v2.3/2.4 tag (text information frames only -- see
        /// <c>Codecs.Mp3.Id3v2Reader</c>'s own doc comment for the exact scope); null for every
        /// other format, and for a WAV/MP3 file that simply doesn't carry one.
        /// </summary>
        public IReadOnlyDictionary<string, string>? Tags { get; init; }
    }
}
