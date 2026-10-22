namespace EggEncoder.Live.Rtmp
{
    /// <summary>
    /// One complete RTMP message, reassembled from (or about to be split into) chunks (Adobe RTMP
    /// specification 1.0, section 6.1).
    /// </summary>
    /// <param name="Type">The message type id; values outside <see cref="RtmpMessageType"/>'s named members are kept as-is.</param>
    /// <param name="StreamId">The message stream id: 0 for the connection's own control and command messages, the id <c>createStream</c> returned for a stream's media.</param>
    /// <param name="Timestamp">Milliseconds, 32 bits, wrapping; absolute, already resolved from the chunk headers' deltas.</param>
    /// <param name="Payload">The message body, owned by the message and never modified afterwards.</param>
    public readonly record struct RtmpMessage(RtmpMessageType Type, uint StreamId, uint Timestamp, ReadOnlyMemory<byte> Payload)
    {
        /// <summary>The largest payload the 24-bit message length field can describe.</summary>
        public const int MaxPayloadLength = 0xFFFFFF;
    }
}
