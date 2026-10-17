namespace EggEncoder.Streaming.Hls
{
    /// <summary>One audio rendition's own entry in a master playlist.</summary>
    /// <param name="Uri">The rendition's own media playlist URI.</param>
    /// <param name="BandwidthBitsPerSecond"><c>BANDWIDTH</c> -- a peak bitrate estimate in bits per second.</param>
    /// <param name="CodecsString"><c>CODECS</c> -- an RFC 6381 codec string, e.g. <c>"mp4a.40.2"</c> for AAC-LC.</param>
    public readonly record struct HlsRenditionDescriptor(string Uri, int BandwidthBitsPerSecond, string CodecsString);
}
