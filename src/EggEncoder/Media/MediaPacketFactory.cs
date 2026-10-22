using EggEncoder.Streaming;

namespace EggEncoder.Media
{
    /// <summary>
    /// Bridges this project's encoder output into the packet model, so every existing
    /// <see cref="IStreamingEncoder"/> can feed a packet pipeline unchanged. See
    /// docs/live-ingest-restream-plan.md item 1.
    /// </summary>
    public static class MediaPacketFactory
    {
        /// <summary>
        /// Copies one <see cref="EncodedAudioFrame"/> into an owned <see cref="MediaPacket"/>, safe to
        /// keep after the encoder callback returns. Audio frames are always random-access points, and
        /// carry no B-frame reordering, so <c>Dts == Pts</c>.
        /// </summary>
        /// <param name="frame">The encoder's frame; its payload is copied.</param>
        /// <param name="trackId">The audio track's <see cref="MediaTrackInfo.TrackId"/>.</param>
        /// <param name="pts">
        /// The frame's presentation time in the track's timescale. For a track whose timescale is the
        /// sample rate, that is the running total of the earlier frames' <see cref="EncodedAudioFrame.SampleCount"/>.
        /// </param>
        /// <param name="configVersion">The track's current <see cref="MediaTrackInfo.ConfigVersion"/>.</param>
        public static MediaPacket FromEncodedAudioFrame(EncodedAudioFrame frame, int trackId, long pts, int configVersion = 0)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(trackId);
            ArgumentOutOfRangeException.ThrowIfNegative(configVersion);

            return new MediaPacket(trackId, pts, pts, IsKeyframe: true, configVersion, frame.Payload.ToArray());
        }
    }
}
