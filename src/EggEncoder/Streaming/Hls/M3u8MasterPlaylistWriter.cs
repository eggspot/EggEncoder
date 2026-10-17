using System.Text;

namespace EggEncoder.Streaming.Hls
{
    /// <summary>
    /// Formats an HLS master playlist (RFC 8216 §4.3.4.2) listing multiple audio-bitrate renditions,
    /// each a complete, independent audio rendition rather than an alternate track alongside video
    /// -- the simpler structure real-world podcast/radio HLS output commonly uses. The
    /// <c>EXT-X-MEDIA:TYPE=AUDIO</c> grouped-rendition form (for a video-plus-alternate-audio-track
    /// scenario) is a deliberately deferred option this writer does not produce; see
    /// docs/streaming-plan.md item 5.
    /// </summary>
    public static class M3u8MasterPlaylistWriter
    {
        public static string BuildMasterPlaylist(IReadOnlyList<HlsRenditionDescriptor> renditions)
        {
            ArgumentNullException.ThrowIfNull(renditions);

            var text = new StringBuilder();

            text.Append("#EXTM3U\n");
            text.Append("#EXT-X-VERSION:").Append(M3u8PlaylistWriter.Version).Append('\n');

            foreach (var rendition in renditions)
            {
                text.Append("#EXT-X-STREAM-INF:BANDWIDTH=").Append(rendition.BandwidthBitsPerSecond)
                    .Append(",CODECS=\"").Append(rendition.CodecsString).Append("\"\n");
                text.Append(rendition.Uri).Append('\n');
            }

            return text.ToString();
        }
    }
}
