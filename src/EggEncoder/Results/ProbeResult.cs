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

        public string? WaveformResult { get; set; }
    }
}
