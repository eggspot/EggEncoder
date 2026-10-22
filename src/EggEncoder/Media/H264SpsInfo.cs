namespace EggEncoder.Media
{
    /// <summary>
    /// The header fields of one H.264 sequence parameter set that a muxer or playlist needs, as
    /// <see cref="H264SpsParser.Parse"/> reads them. Display dimensions already have the SPS frame
    /// cropping applied (1920x1080 coded as 1088 rows reports 1080).
    /// </summary>
    public sealed record H264SpsInfo(
        int ProfileIdc,
        int ConstraintFlags,
        int LevelIdc,
        int ChromaFormatIdc,
        int BitDepthLuma,
        int BitDepthChroma,
        int Width,
        int Height)
    {
        /// <summary>The RFC 6381 codec string for an HLS <c>CODECS</c> attribute or an MP4 sample entry, e.g. <c>avc1.64001F</c>.</summary>
        public string CodecsString => $"avc1.{ProfileIdc:X2}{ConstraintFlags:X2}{LevelIdc:X2}";
    }
}
