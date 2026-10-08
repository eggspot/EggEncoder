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
    }
}
