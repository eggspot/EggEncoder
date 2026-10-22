namespace EggEncoder.Media
{
    /// <summary>
    /// The codec of an already-encoded <see cref="MediaPacket"/> payload. Video codecs are
    /// passthrough only: their payloads are remuxed, never decoded or encoded (see
    /// docs/live-ingest-restream-plan.md "Scope philosophy").
    /// </summary>
    public enum MediaCodec
    {
        /// <summary>AAC; payload is one raw access unit with no ADTS header, configuration is an AudioSpecificConfig.</summary>
        Aac,
        Mp3,
        Opus,

        /// <summary>H.264/AVC; payload is length-prefixed (AVCC), configuration is an <see cref="AvcDecoderConfigurationRecord"/>.</summary>
        H264,

        /// <summary>H.265/HEVC; payload is length-prefixed (HVCC), configuration is an <see cref="HevcDecoderConfigurationRecord"/>.</summary>
        H265,
        Av1,
        Vp9
    }
}
