namespace EggEncoder.Streaming.Hls
{
    /// <summary>One media segment's own entry in a media playlist.</summary>
    public readonly record struct HlsSegmentDescriptor(long SequenceNumber, double DurationSeconds, string Uri)
    {
        /// <summary>
        /// Writes <c>EXT-X-DISCONTINUITY</c> before this segment: its encoding parameters differ
        /// from the previous segment's (RFC 8216 §4.3.2.3), so players reset their decoders here.
        /// </summary>
        public bool Discontinuity { get; init; }

        /// <summary>
        /// The init segment (<c>EXT-X-MAP</c>) this segment needs, or <c>null</c> for the one
        /// already in effect. <see cref="M3u8PlaylistWriter"/> writes <c>EXT-X-MAP</c> again
        /// before this segment when it differs from the previous one.
        /// </summary>
        public string? InitSegmentUri { get; init; }
    }
}
