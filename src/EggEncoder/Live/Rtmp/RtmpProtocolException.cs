namespace EggEncoder.Live.Rtmp
{
    /// <summary>
    /// A peer broke the RTMP protocol or one of this library's bounds on it: a malformed handshake, a
    /// chunk header on a chunk stream that was never started, a message or chunk size over the
    /// configured limit, an undecodable command. The connection the bytes came from can't be trusted
    /// afterwards and should be closed. See docs/live-ingest-restream-plan.md "Security posture".
    /// </summary>
    public sealed class RtmpProtocolException : IOException
    {
        /// <summary>Creates the exception with a message describing what the peer got wrong.</summary>
        public RtmpProtocolException(string message)
            : base(message)
        {
        }

        /// <summary>Creates the exception wrapping the lower-level error (typically an AMF0 <see cref="InvalidDataException"/>).</summary>
        public RtmpProtocolException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
