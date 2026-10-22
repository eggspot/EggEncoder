namespace EggEncoder.Media
{
    /// <summary>
    /// One already-encoded access unit of any codec, as every source produces and every output
    /// consumes it (docs/live-ingest-restream-plan.md item 1). "Packet" is container-level and
    /// codec-agnostic, deliberately a different word from the encoder-output
    /// <see cref="Streaming.EncodedAudioFrame"/>; <see cref="MediaPacketFactory"/> converts one to
    /// the other.
    /// </summary>
    /// <param name="TrackId">The <see cref="MediaTrackInfo.TrackId"/> this packet belongs to.</param>
    /// <param name="Pts">Presentation timestamp, in ticks of that track's <see cref="MediaTrackInfo.Timescale"/>.</param>
    /// <param name="Dts">Decode timestamp, same timescale. Equal to <paramref name="Pts"/> for audio and for video without B-frames.</param>
    /// <param name="IsKeyframe">True for a random-access point (an IDR/IRAP video frame; every audio packet).</param>
    /// <param name="ConfigVersion">The <see cref="MediaTrackInfo.ConfigVersion"/> this packet was encoded against.</param>
    /// <param name="Payload">
    /// Owned and immutable: one packet may be shared across several outputs. Length-prefixed
    /// (AVCC/HVCC, 4-byte lengths) for H.264/H.265; a raw access unit (no ADTS) for AAC.
    /// </param>
    public readonly record struct MediaPacket(
        int TrackId,
        long Pts,
        long Dts,
        bool IsKeyframe,
        int ConfigVersion,
        ReadOnlyMemory<byte> Payload)
    {
        /// <summary><see cref="Pts"/> minus <see cref="Dts"/>: FLV's CompositionTime, MP4's ctts offset.</summary>
        public long CompositionOffset => Pts - Dts;
    }
}
