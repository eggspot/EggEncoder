namespace EggEncoder.Live.Rtmp
{
    /// <summary>The limit type of a Set Peer Bandwidth message (Adobe RTMP specification 1.0, section 5.4.5).</summary>
    public enum RtmpPeerBandwidthLimitType : byte
    {
        /// <summary>Limit output bandwidth to the window size given.</summary>
        Hard = 0,

        /// <summary>Limit output bandwidth to the window size given or the limit already in effect, whichever is smaller.</summary>
        Soft = 1,

        /// <summary>Treat as <see cref="Hard"/> if the previous limit was hard, otherwise ignore the message.</summary>
        Dynamic = 2
    }
}
