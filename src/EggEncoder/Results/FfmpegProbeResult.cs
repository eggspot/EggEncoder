using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

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

    internal class FfmpegProbeResult
    {
        [JsonPropertyName("streams")]
        public FfmpegProbeStreamResult[]? Streams { get; set; }

        [JsonPropertyName("format")]
        public FfmpegProbeFormatResult? Format { get; set; }
    }

    internal class FfmpegProbeFormatResult
    {
        [JsonPropertyName("bit_rate")]
        public JsonNode? BitRate { get; set; }

        [JsonPropertyName("duration")]
        public string? DurationInSeconds { get; set; }

        [JsonPropertyName("format_long_name")]
        public JsonNode? FormatLongName { get; set; }

        [JsonPropertyName("format_name")]
        public JsonNode? FormatName { get; set; }

        [JsonPropertyName("nb_programs")]
        public JsonNode? NbPrograms { get; set; }

        [JsonPropertyName("nb_streams")]
        public JsonNode? NbStreams { get; set; }

        [JsonPropertyName("probe_score")]
        public JsonNode? ProbeScore { get; set; }

        [JsonPropertyName("size")]
        public JsonNode? Size { get; set; }

        [JsonPropertyName("start_time")]
        public JsonNode? StartTime { get; set; }

        [JsonPropertyName("tags")]
        public JsonNode? Tags { get; set; }
    }

    internal class FfmpegProbeStreamResult
    {
        [JsonPropertyName("avg_frame_rate")]
        public JsonNode? AvgFrameRate { get; set; }

        [JsonPropertyName("bit_rate")]
        public string? BitRate { get; set; }

        [JsonPropertyName("bits_per_raw_sample")]
        public string? BitsPerRawSample { get; set; }

        [JsonPropertyName("bits_per_sample")]
        public int? BitsPerSample { get; set; }

        [JsonPropertyName("channel_layout")]
        public JsonNode? ChannelLayout { get; set; }

        [JsonPropertyName("channels")]
        public JsonNode? Channels { get; set; }

        [JsonPropertyName("codec_long_name")]
        public JsonNode? CodecLongName { get; set; }

        [JsonPropertyName("codec_name")]
        public JsonNode? CodecName { get; set; }

        [JsonPropertyName("codec_tag")]
        public JsonNode? CodecTag { get; set; }

        [JsonPropertyName("codec_tag_string")]
        public JsonNode? CodecTagString { get; set; }

        [JsonPropertyName("codec_type")]
        public JsonNode? CodecType { get; set; }

        [JsonPropertyName("duration")]
        public JsonNode? Duration { get; set; }

        [JsonPropertyName("duration_ts")]
        public JsonNode? DurationTs { get; set; }

        [JsonPropertyName("index")]
        public JsonNode? Index { get; set; }

        [JsonPropertyName("initial_padding")]
        public JsonNode? InitialPadding { get; set; }

        [JsonPropertyName("r_frame_rate")]
        public JsonNode? RFrameRate { get; set; }

        [JsonPropertyName("sample_fmt")]
        public JsonNode? SampleFmt { get; set; }

        [JsonPropertyName("sample_rate")]
        public string? SampleRate { get; set; }

        [JsonPropertyName("start_pts")]
        public JsonNode? StartPts { get; set; }

        [JsonPropertyName("start_time")]
        public JsonNode? StartTime { get; set; }

        [JsonPropertyName("time_base")]
        public JsonNode? TimeBase { get; set; }

        [JsonPropertyName("disposition")]
        public JsonNode? Disposition { get; set; }

        [JsonPropertyName("width")]
        public int? Width { get; set; }

        [JsonPropertyName("height")]
        public int? Height { get; set; }

        public bool HasSampleRate() => !string.IsNullOrWhiteSpace(BitsPerRawSample) || BitsPerSample.HasValue;
    }
}
