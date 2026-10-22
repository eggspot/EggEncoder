namespace EggEncoder.Containers.Flv
{
    /// <summary>What one FLV audio or video tag body carries, as <see cref="FlvTagBodyParser"/> classifies it.</summary>
    public enum FlvPacketType
    {
        /// <summary>Codec configuration: an avcC/hvcC/AV1/VP9 record or an AAC AudioSpecificConfig.</summary>
        SequenceHeader,
        /// <summary>One coded access unit (a video frame, or an AAC/MP3 audio frame).</summary>
        CodedFrame,
        /// <summary>The end of a video sequence; carries no media.</summary>
        SequenceEnd,
        /// <summary>A valid tag that carries no media: a video info/command frame or an Enhanced-FLV metadata packet.</summary>
        Ignored,
        /// <summary>A codec or packet layout this parser doesn't handle; <see cref="FlvTagBody.UnsupportedReason"/> says which.</summary>
        Unsupported
    }
}
