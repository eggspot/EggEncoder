namespace EggEncoder.Media
{
    /// <summary>
    /// Describes one track of a packet stream: what it carries, the timescale its packets'
    /// <see cref="MediaPacket.Pts"/>/<see cref="MediaPacket.Dts"/> are in, and its codec
    /// configuration. Configuration is track-level, never in-band: a mid-stream change (a new FLV
    /// sequence header, a new SPS) produces a new instance with a higher <see cref="ConfigVersion"/>,
    /// and packets carry the version they belong to. See docs/live-ingest-restream-plan.md item 1.
    /// </summary>
    /// <remarks>
    /// Record equality compares <see cref="CodecConfiguration"/> as a <see cref="ReadOnlyMemory{T}"/>
    /// (same backing buffer and range), not byte by byte; compare <see cref="ConfigVersion"/> to tell
    /// whether a configuration changed.
    /// </remarks>
    public sealed record MediaTrackInfo
    {
        private readonly int _trackId;
        private readonly int _timescale;
        private readonly int _configVersion;
        private readonly int _sampleRate;
        private readonly int _channels;
        private readonly int _width;
        private readonly int _height;

        /// <param name="trackId">Identifies the track within its <see cref="MediaTrackSet"/>; non-negative.</param>
        /// <param name="kind">Audio, video or data.</param>
        /// <param name="codec">The codec of every packet on this track.</param>
        /// <param name="timescale">Ticks per second for this track's timestamps (1000 for RTMP/FLV, 90000 for MPEG-TS, the sample rate for audio from this project's encoders); positive.</param>
        /// <param name="codecConfiguration">avcC / hvcC / AudioSpecificConfig bytes; empty when the codec needs none.</param>
        /// <param name="configVersion">Increases each time this track's configuration changes; non-negative.</param>
        /// <param name="sampleRate">Audio sample rate in Hz, or 0 when unknown / not audio.</param>
        /// <param name="channels">Audio channel count, or 0 when unknown / not audio.</param>
        /// <param name="width">Video display width in pixels, or 0 when unknown / not video.</param>
        /// <param name="height">Video display height in pixels, or 0 when unknown / not video.</param>
        public MediaTrackInfo(
            int trackId,
            MediaKind kind,
            MediaCodec codec,
            int timescale,
            ReadOnlyMemory<byte> codecConfiguration,
            int configVersion,
            int sampleRate = 0,
            int channels = 0,
            int width = 0,
            int height = 0)
        {
            TrackId = trackId;
            Kind = kind;
            Codec = codec;
            Timescale = timescale;
            CodecConfiguration = codecConfiguration;
            ConfigVersion = configVersion;
            SampleRate = sampleRate;
            Channels = channels;
            Width = width;
            Height = height;
        }

        public int TrackId { get => _trackId; init => _trackId = RequireNonNegative(value, nameof(TrackId)); }

        public MediaKind Kind { get; init; }

        public MediaCodec Codec { get; init; }

        public int Timescale
        {
            get => _timescale;
            init => _timescale = value > 0
                ? value
                : throw new ArgumentOutOfRangeException(nameof(Timescale), value, "Timescale must be positive");
        }

        public ReadOnlyMemory<byte> CodecConfiguration { get; init; }

        public int ConfigVersion { get => _configVersion; init => _configVersion = RequireNonNegative(value, nameof(ConfigVersion)); }

        public int SampleRate { get => _sampleRate; init => _sampleRate = RequireNonNegative(value, nameof(SampleRate)); }

        public int Channels { get => _channels; init => _channels = RequireNonNegative(value, nameof(Channels)); }

        public int Width { get => _width; init => _width = RequireNonNegative(value, nameof(Width)); }

        public int Height { get => _height; init => _height = RequireNonNegative(value, nameof(Height)); }

        private static int RequireNonNegative(int value, string name) =>
            value >= 0 ? value : throw new ArgumentOutOfRangeException(name, value, $"{name} must not be negative");
    }
}
