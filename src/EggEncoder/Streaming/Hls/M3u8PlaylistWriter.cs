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
        /// The <c>EXT-X-VERSION</c> every playlist this writer produces declares. Confirmed
        /// against a real ffmpeg-produced fMP4 HLS fixture using the exact same combination this
        /// writer itself needs (fMP4 segments + <c>EXT-X-MAP</c>, no <c>EXT-X-I-FRAMES-ONLY</c>) --
        /// not derived from reading RFC 8216 §7's own compatibility table alone, since a prior
        /// version of this constant (<c>6</c>) was "corrected" down from <c>7</c> based on exactly
        /// that unverified reading and was wrong; real-world encoders consistently declare <c>7</c>
        /// for this combination.
        /// </summary>
        public const int Version = 7;

        /// <param name="targetDurationSeconds">
        /// <c>EXT-X-TARGETDURATION</c> -- an integer number of seconds no shorter than any segment's
        /// own duration (RFC 8216 §4.3.3.1).
        /// </param>
        /// <param name="mediaSequence"><c>EXT-X-MEDIA-SEQUENCE</c> -- the sequence number of the first segment still listed.</param>
        /// <param name="segments">In playback order; empty is valid (a not-yet-started live playlist, or a VOD stream with no content).</param>
        /// <param name="initSegmentUri"><c>EXT-X-MAP</c>'s own URI, or <c>null</c> to omit the tag (a non-fMP4 segment format needs none). A segment that names its own <see cref="HlsSegmentDescriptor.InitSegmentUri"/>, or is flagged <see cref="HlsSegmentDescriptor.Discontinuity"/>, gets its tags as the other overload describes.</param>
        /// <param name="endList">Writes <c>EXT-X-ENDLIST</c> when true -- a VOD playlist, or a live one whose broadcast has ended. Never true for an in-progress live playlist.</param>
        public static string BuildMediaPlaylist(
            int targetDurationSeconds,
            long mediaSequence,
            IReadOnlyList<HlsSegmentDescriptor> segments,
            string? initSegmentUri,
            bool endList) =>
            BuildMediaPlaylist(targetDurationSeconds, mediaSequence, discontinuitySequence: 0, segments, initSegmentUri, endList);

        /// <summary>
        /// The same playlist, for a stream whose encoding parameters change: writes
        /// <c>EXT-X-DISCONTINUITY</c> before every segment flagged
        /// <see cref="HlsSegmentDescriptor.Discontinuity"/>, and <c>EXT-X-MAP</c> again before every
        /// segment whose <see cref="HlsSegmentDescriptor.InitSegmentUri"/> differs from the init
        /// segment in effect (RFC 8216 §4.3.2.5).
        /// </summary>
        /// <param name="targetDurationSeconds"><c>EXT-X-TARGETDURATION</c>, as in the other overload.</param>
        /// <param name="mediaSequence"><c>EXT-X-MEDIA-SEQUENCE</c>, as in the other overload.</param>
        /// <param name="discontinuitySequence">
        /// <c>EXT-X-DISCONTINUITY-SEQUENCE</c>: how many <c>EXT-X-DISCONTINUITY</c> tags have left a
        /// live window so far (RFC 8216 §6.2.2). Written only when it isn't 0, the value a playlist
        /// without the tag implies; must not be negative.
        /// </param>
        /// <param name="segments">In playback order; empty is valid.</param>
        /// <param name="initSegmentUri">The init segment in effect for the first listed segment (a segment's own <see cref="HlsSegmentDescriptor.InitSegmentUri"/> overrides it), or <c>null</c> to omit <c>EXT-X-MAP</c> until a segment names one.</param>
        /// <param name="endList">Writes <c>EXT-X-ENDLIST</c> when true.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="discontinuitySequence"/> is negative.</exception>
        public static string BuildMediaPlaylist(
            int targetDurationSeconds,
            long mediaSequence,
            long discontinuitySequence,
            IReadOnlyList<HlsSegmentDescriptor> segments,
            string? initSegmentUri,
            bool endList)
        {
            ArgumentNullException.ThrowIfNull(segments);
            ArgumentOutOfRangeException.ThrowIfNegative(discontinuitySequence);

            var text = new StringBuilder();

            text.Append("#EXTM3U\n");
            text.Append("#EXT-X-VERSION:").Append(Version).Append('\n');
            text.Append("#EXT-X-TARGETDURATION:").Append(targetDurationSeconds).Append('\n');
            text.Append("#EXT-X-MEDIA-SEQUENCE:").Append(mediaSequence).Append('\n');
            if (discontinuitySequence > 0)
            {
                text.Append("#EXT-X-DISCONTINUITY-SEQUENCE:").Append(discontinuitySequence).Append('\n');
            }

            var currentInitSegmentUri = initSegmentUri;
            if (currentInitSegmentUri is not null)
            {
                AppendMap(text, currentInitSegmentUri);
            }

            foreach (var segment in segments)
            {
                if (segment.Discontinuity)
                {
                    text.Append("#EXT-X-DISCONTINUITY\n");
                }

                if (segment.InitSegmentUri is { } segmentInitUri && segmentInitUri != currentInitSegmentUri)
                {
                    AppendMap(text, segmentInitUri);
                    currentInitSegmentUri = segmentInitUri;
                }

                text.Append("#EXTINF:").Append(segment.DurationSeconds.ToString("0.000000", CultureInfo.InvariantCulture)).Append(",\n");
                text.Append(segment.Uri).Append('\n');
            }

            if (endList)
            {
                text.Append("#EXT-X-ENDLIST\n");
            }

            return text.ToString();
        }

        private static void AppendMap(StringBuilder text, string initSegmentUri) =>
            text.Append("#EXT-X-MAP:URI=\"").Append(initSegmentUri).Append("\"\n");
    }
}
