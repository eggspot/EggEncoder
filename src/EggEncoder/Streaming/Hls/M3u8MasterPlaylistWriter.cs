using System.Text;
using EggEncoder.Media;

namespace EggEncoder.Streaming.Hls
{
    /// <summary>
    /// Formats an HLS master playlist (RFC 8216 §4.3.4.2) listing multiple renditions, each a
    /// complete, independent rendition rather than an alternate track alongside video -- the
    /// simpler structure real-world podcast/radio HLS output commonly uses. The
    /// <c>EXT-X-MEDIA:TYPE=AUDIO</c> grouped-rendition form (for a video-plus-alternate-audio-track
    /// scenario) is a deliberately deferred option this writer does not produce; see
    /// docs/streaming-plan.md item 5.
    /// </summary>
    public static class M3u8MasterPlaylistWriter
    {
        private const string TracksParameter = "tracks";

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

        /// <summary>
        /// Builds a rendition's <c>CODECS</c> value (RFC 6381) from its tracks, for
        /// <see cref="HlsRenditionDescriptor.CodecsString"/>: video tracks first, then audio, each
        /// distinct string once, comma-separated. H.264 gives <c>avc1.PPCCLL</c> from the avcC
        /// record's first SPS, H.265 <c>hvc1.…</c> from the hvcC record's SPS (the sample entry the
        /// fMP4 writer uses), and AAC <c>mp4a.40.N</c> with N the AudioSpecificConfig's audio object
        /// type (2 for AAC-LC, 5 or 29 for HE-AAC signalled explicitly).
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="tracks"/> is empty, or a track's codec configuration or SPS can't be read.</exception>
        /// <exception cref="NotSupportedException">A track's codec is not H.264, H.265 or AAC.</exception>
        public static string BuildCodecsString(MediaTrackSet tracks)
        {
            ArgumentNullException.ThrowIfNull(tracks);
            if (tracks.Tracks.Count == 0)
            {
                throw new ArgumentException("A CODECS value needs at least one track.", TracksParameter);
            }

            var codecs = tracks.Tracks
                .OrderBy(track => track.Codec is MediaCodec.H264 or MediaCodec.H265 ? 0 : 1)
                .Select(CodecsStringOf)
                .Distinct();
            return string.Join(',', codecs);
        }

        private static string CodecsStringOf(MediaTrackInfo track)
        {
            if (track.Codec is not (MediaCodec.H264 or MediaCodec.H265 or MediaCodec.Aac))
            {
                throw new NotSupportedException($"{track.Codec} has no HLS CODECS value here; only H.264, H.265 and AAC do.");
            }

            var configuration = track.CodecConfiguration.Span;
            try
            {
                return track.Codec switch
                {
                    MediaCodec.H264 => H264SpsParser.Parse(AvcDecoderConfigurationRecord.Parse(configuration).SequenceParameterSets[0].Span).CodecsString,
                    MediaCodec.H265 => H265SpsParser.Parse(HevcDecoderConfigurationRecord.Parse(configuration).GetNalUnits(HevcDecoderConfigurationRecord.SpsNalUnitType)[0].Span).CodecsString,
                    _ => $"mp4a.40.{AudioSpecificConfigReader.ReadFields(configuration).ObjectType}"
                };
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException)
            {
                throw new ArgumentException($"{track.Codec} track {track.TrackId}'s codec configuration can't be read: {e.Message}", TracksParameter, e);
            }
        }
    }
}
