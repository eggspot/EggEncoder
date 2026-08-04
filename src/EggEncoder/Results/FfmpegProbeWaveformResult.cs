using System.Text.Json.Serialization;

namespace EggEncoder.Results
{
    internal class FfmpegProbeWaveformResult
    {
        [JsonPropertyName("frames")]
        public FfmpegProbeWaveformFrameResult[]? Frames { get; set; }

        public List<double> Values => [.. Frames?.Select(f => f.Tags?.Value ?? 0) ?? []];
    }

    internal class FfmpegProbeWaveformFrameResult
    {
        [JsonPropertyName("tags")]
        public FfmpegProbeWaveformFrameTagResult? Tags { get; set; }
    }

    internal class FfmpegProbeWaveformFrameTagResult
    {
        [JsonPropertyName("lavfi.astats.Overall.RMS_difference")]
        [JsonInclude]
        private string? _value { get; set; }

        [JsonIgnore]
        public double? Value => string.IsNullOrWhiteSpace(_value) ? null : double.Parse(_value);
    }
}
