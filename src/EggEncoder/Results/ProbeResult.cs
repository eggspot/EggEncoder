namespace EggEncoder.Results
{
    public class ProbeResult
    {
        // Only populated with values this library can genuinely compute -- fields ffprobe
        // reports that don't have a real equivalent here (probe_score, disposition flags,
        // container timestamps) are intentionally omitted rather than filled with placeholders.
        public required ProbeFormatInfo Format { get; set; }

        public required ProbeStreamInfo Stream { get; set; }

        public string? WaveformResult { get; set; }
    }

    public class ProbeFormatInfo
    {
        public required string FormatName { get; init; }

        public required string FormatLongName { get; init; }

        public required long SizeBytes { get; init; }

        public required double DurationSeconds { get; init; }

        public required int StreamCount { get; init; }
    }

    public class ProbeStreamInfo
    {
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
    }
}
