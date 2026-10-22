namespace EggEncoder.Live.Rtmp
{
    /// <summary>
    /// The RTMP message type ids (Adobe RTMP specification 1.0, sections 5.4, 6.2 and 7.1). Other
    /// values can arrive on the wire and are passed through as-is; <see cref="RtmpChunkReader"/> only
    /// acts on <see cref="SetChunkSize"/>, <see cref="Abort"/> and <see cref="WindowAcknowledgementSize"/>.
    /// </summary>
    public enum RtmpMessageType : byte
    {
        /// <summary>Protocol control: the sender's new maximum chunk size.</summary>
        SetChunkSize = 1,

        /// <summary>Protocol control: discard the partially received message on a chunk stream.</summary>
        Abort = 2,

        /// <summary>Protocol control: the number of bytes received so far.</summary>
        Acknowledgement = 3,

        /// <summary>A user control event (Stream Begin, Stream EOF, Ping, ...); see <see cref="RtmpUserControl"/>.</summary>
        UserControl = 4,

        /// <summary>Protocol control: how many bytes the receiver may take before it must acknowledge.</summary>
        WindowAcknowledgementSize = 5,

        /// <summary>Protocol control: limit the peer's output bandwidth.</summary>
        SetPeerBandwidth = 6,

        /// <summary>Audio data: the body is an FLV <c>AUDIODATA</c> tag body.</summary>
        Audio = 8,

        /// <summary>Video data: the body is an FLV <c>VIDEODATA</c> tag body.</summary>
        Video = 9,

        /// <summary>An AMF3-encoded data message.</summary>
        DataAmf3 = 15,

        /// <summary>An AMF3-encoded shared object message.</summary>
        SharedObjectAmf3 = 16,

        /// <summary>An AMF3-encoded command message.</summary>
        CommandAmf3 = 17,

        /// <summary>An AMF0-encoded data message (<c>@setDataFrame</c>/<c>onMetaData</c>); see <see cref="RtmpDataMessage"/>.</summary>
        DataAmf0 = 18,

        /// <summary>An AMF0-encoded shared object message.</summary>
        SharedObjectAmf0 = 19,

        /// <summary>An AMF0-encoded command message (<c>connect</c>, <c>publish</c>, ...); see <see cref="RtmpCommand"/>.</summary>
        CommandAmf0 = 20,

        /// <summary>An aggregate message: several audio/video/data sub-messages in one body.</summary>
        Aggregate = 22
    }
}
