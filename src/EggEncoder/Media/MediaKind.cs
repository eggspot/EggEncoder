namespace EggEncoder.Media
{
    /// <summary>What a <see cref="MediaTrackInfo"/> carries. See docs/live-ingest-restream-plan.md item 1.</summary>
    public enum MediaKind
    {
        Audio,
        Video,

        /// <summary>
        /// Reserved for timed-metadata tracks (onMetaData updates, ID3 passthrough). No
        /// <see cref="MediaCodec"/> describes a data payload yet, so no source produces this today;
        /// metadata is surfaced out of band until one does.
        /// </summary>
        Data
    }
}
