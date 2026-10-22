namespace EggEncoder.Live.Rtmp
{
    /// <summary>
    /// The user control event types (Adobe RTMP specification 1.0, section 7.1.7). Other values can
    /// arrive on the wire and are kept as-is by <see cref="RtmpUserControl.Read"/>.
    /// </summary>
    public enum RtmpUserControlEventType : ushort
    {
        /// <summary>The server tells the client a stream has become functional; the value is the stream id.</summary>
        StreamBegin = 0,

        /// <summary>Playback of the stream (the value) is over.</summary>
        StreamEof = 1,

        /// <summary>There is no more data on the stream (the value) for now.</summary>
        StreamDry = 2,

        /// <summary>The client's buffer length, in milliseconds, for the stream (the value).</summary>
        SetBufferLength = 3,

        /// <summary>The stream (the value) is a recorded stream.</summary>
        StreamIsRecorded = 4,

        /// <summary>The server checks the client is reachable; the value is a timestamp to echo.</summary>
        PingRequest = 6,

        /// <summary>The client's reply to <see cref="PingRequest"/>, echoing its timestamp.</summary>
        PingResponse = 7
    }
}
