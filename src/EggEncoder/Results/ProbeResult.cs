namespace EggEncoder.Results
{
    public class ProbeResult
    {
        public required int DurationInSeconds { get; set; }

        public required int? BitsPerSample { get; set; }

        public required int? BitRate { get; set; }

        public required int? SampleRate { get; set; }

        public required int? Height { get; set; }

        public required int? Width { get; set; }

        public required string? Result { get; set; }

        public string? WaveformResult { get; set; }
    }
}
