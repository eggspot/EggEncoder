namespace EggEncoder.Media
{
    /// <summary>
    /// The header fields of one H.265 sequence parameter set that a muxer or playlist needs, as
    /// <see cref="H265SpsParser.Parse"/> reads them. Display dimensions already have the SPS
    /// conformance window applied.
    /// </summary>
    public sealed record H265SpsInfo(
        HevcProfileTierLevel ProfileTierLevel,
        int ChromaFormatIdc,
        int BitDepthLuma,
        int BitDepthChroma,
        int Width,
        int Height)
    {
        /// <summary>The RFC 6381 codec string with an <c>hvc1</c> sample entry, e.g. <c>hvc1.1.6.L120.90</c>.</summary>
        public string CodecsString => ProfileTierLevel.ToCodecsString();
    }
}
