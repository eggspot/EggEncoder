namespace EggEncoder.Streaming.Hls
{
    /// <summary>Which container <see cref="HlsSegmenter"/> packages media segments into.</summary>
    public enum SegmentFormat
    {
        /// <summary>fMP4/CMAF (<c>moof</c>+<c>mdat</c>, via <see cref="Mp4.FmpMediaSegmentWriter"/>) -- needs an init segment, referenced from the playlist via <c>EXT-X-MAP</c>.</summary>
        Fmp4,

        /// <summary>MPEG-TS (via <see cref="MpegTs.MpegTsMuxer"/>) -- PAT/PMT repeated in every segment, so no separate init segment or <c>EXT-X-MAP</c> tag is needed.</summary>
        MpegTs
    }
}
