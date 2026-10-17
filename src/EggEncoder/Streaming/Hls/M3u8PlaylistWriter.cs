using System.Globalization;
using System.Text;

namespace EggEncoder.Streaming.Hls
{
    /// <summary>
    /// Formats an HLS media playlist (RFC 8216 §4.3.3/§4.4) as UTF-8 text. A pure, stateless
    /// formatter -- <see cref="HlsSegmenter"/> owns the segment-boundary/live-window policy and
    /// calls this once per playlist change.
    /// </summary>
    public static class M3u8PlaylistWriter
    {
        /// <summary>
        /// The minimum <c>EXT-X-VERSION</c> an <c>EXT-X-MAP</c> tag requires without
        /// <c>EXT-X-I-FRAMES-ONLY</c> present (RFC 8216 §7) -- the version every playlist this
        /// writer produces declares, since every fMP4-segmented playlist needs <c>EXT-X-MAP</c>.
        /// </summary>
        public const int Version = 6;

        /// <param name="targetDurationSeconds">
        /// <c>EXT-X-TARGETDURATION</c> -- an integer number of seconds no shorter than any segment's
        /// own duration (RFC 8216 §4.3.3.1).
        /// </param>
        /// <param name="mediaSequence"><c>EXT-X-MEDIA-SEQUENCE</c> -- the sequence number of the first segment still listed.</param>
        /// <param name="segments">In playback order; empty is valid (a not-yet-started live playlist, or a VOD stream with no content).</param>
        /// <param name="initSegmentUri"><c>EXT-X-MAP</c>'s own URI, or <c>null</c> to omit the tag entirely (a non-fMP4 segment format needs none).</param>
        /// <param name="endList">Writes <c>EXT-X-ENDLIST</c> when true -- a VOD playlist, or a live one whose broadcast has ended. Never true for an in-progress live playlist.</param>
        public static string BuildMediaPlaylist(
            int targetDurationSeconds,
            long mediaSequence,
            IReadOnlyList<HlsSegmentDescriptor> segments,
            string? initSegmentUri,
            bool endList)
        {
            ArgumentNullException.ThrowIfNull(segments);

            var text = new StringBuilder();

            text.Append("#EXTM3U\n");
            text.Append("#EXT-X-VERSION:").Append(Version).Append('\n');
            text.Append("#EXT-X-TARGETDURATION:").Append(targetDurationSeconds).Append('\n');
            text.Append("#EXT-X-MEDIA-SEQUENCE:").Append(mediaSequence).Append('\n');

            if (initSegmentUri is not null)
            {
                text.Append("#EXT-X-MAP:URI=\"").Append(initSegmentUri).Append("\"\n");
            }

            foreach (var segment in segments)
            {
                text.Append("#EXTINF:").Append(segment.DurationSeconds.ToString("0.000000", CultureInfo.InvariantCulture)).Append(",\n");
                text.Append(segment.Uri).Append('\n');
            }

            if (endList)
            {
                text.Append("#EXT-X-ENDLIST\n");
            }

            return text.ToString();
        }
    }
}
