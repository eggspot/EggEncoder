using System.Buffers.Binary;

namespace EggEncoder.Live.Rtmp
{
    /// <summary>
    /// One RTMP user control event (message type 4, Adobe RTMP specification 1.0, section 7.1.7): a
    /// 16-bit event type, then the event data. Every event this type knows carries one 32-bit value
    /// (a stream id or a timestamp), and Set Buffer Length a second one, the buffer length.
    /// </summary>
    /// <param name="EventType">The event type; values outside <see cref="RtmpUserControlEventType"/>'s named members are kept as-is.</param>
    /// <param name="Value">The stream id, or for Ping Request/Response the timestamp.</param>
    /// <param name="BufferLength">Set Buffer Length's buffer length in milliseconds; 0 for every other event.</param>
    public readonly record struct RtmpUserControl(RtmpUserControlEventType EventType, uint Value, uint BufferLength = 0)
    {
        /// <summary>Stream Begin for <paramref name="streamId"/>.</summary>
        public static RtmpUserControl StreamBegin(uint streamId) => new(RtmpUserControlEventType.StreamBegin, streamId);

        /// <summary>Stream EOF for <paramref name="streamId"/>.</summary>
        public static RtmpUserControl StreamEof(uint streamId) => new(RtmpUserControlEventType.StreamEof, streamId);

        /// <summary>Set Buffer Length: the client buffers <paramref name="bufferLengthMilliseconds"/> of <paramref name="streamId"/>.</summary>
        public static RtmpUserControl SetBufferLength(uint streamId, uint bufferLengthMilliseconds) =>
            new(RtmpUserControlEventType.SetBufferLength, streamId, bufferLengthMilliseconds);

        /// <summary>Ping Request carrying <paramref name="timestamp"/>.</summary>
        public static RtmpUserControl PingRequest(uint timestamp) => new(RtmpUserControlEventType.PingRequest, timestamp);

        /// <summary>Ping Response echoing <paramref name="timestamp"/> from the request.</summary>
        public static RtmpUserControl PingResponse(uint timestamp) => new(RtmpUserControlEventType.PingResponse, timestamp);

        /// <summary>The message carrying this event, on message stream 0.</summary>
        public RtmpMessage ToMessage(uint timestamp = 0)
        {
            var payload = new byte[EventType == RtmpUserControlEventType.SetBufferLength ? 10 : 6];
            BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)EventType);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(2), Value);
            if (EventType == RtmpUserControlEventType.SetBufferLength)
            {
                BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(6), BufferLength);
            }

            return new RtmpMessage(RtmpMessageType.UserControl, 0, timestamp, payload);
        }

        /// <summary>
        /// Reads a user control message payload. Known events must carry their full data; an unknown
        /// event type is kept with <see cref="Value"/> read from its first four data bytes when present.
        /// </summary>
        /// <exception cref="RtmpProtocolException">The payload is shorter than the event needs.</exception>
        public static RtmpUserControl Read(ReadOnlySpan<byte> payload)
        {
            if (payload.Length < 2)
            {
                throw new RtmpProtocolException($"RTMP: user control payload is {payload.Length} bytes, too short for an event type");
            }

            var eventType = (RtmpUserControlEventType)BinaryPrimitives.ReadUInt16BigEndian(payload);
            var data = payload[2..];
            var needed = eventType switch
            {
                RtmpUserControlEventType.SetBufferLength => 8,
                RtmpUserControlEventType.StreamBegin or RtmpUserControlEventType.StreamEof or RtmpUserControlEventType.StreamDry
                    or RtmpUserControlEventType.StreamIsRecorded or RtmpUserControlEventType.PingRequest
                    or RtmpUserControlEventType.PingResponse => 4,
                _ => 0
            };
            if (data.Length < needed)
            {
                throw new RtmpProtocolException($"RTMP: user control event {(ushort)eventType} carries {data.Length} data bytes, needs {needed}");
            }

            var value = data.Length >= 4 ? BinaryPrimitives.ReadUInt32BigEndian(data) : 0;
            var bufferLength = needed == 8 ? BinaryPrimitives.ReadUInt32BigEndian(data[4..]) : 0;
            return new RtmpUserControl(eventType, value, bufferLength);
        }
    }
}
