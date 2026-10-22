using System.Buffers.Binary;

namespace EggEncoder.Live.Rtmp
{
    /// <summary>
    /// Builds and reads the RTMP protocol control messages (Adobe RTMP specification 1.0, section 5.4):
    /// Set Chunk Size, Abort, Acknowledgement, Window Acknowledgement Size and Set Peer Bandwidth. They
    /// always travel on message stream 0 and, by convention, on chunk stream
    /// <see cref="ChunkStreamId"/>. A malformed payload throws <see cref="RtmpProtocolException"/>.
    /// </summary>
    public static class RtmpProtocolControl
    {
        /// <summary>The chunk stream id protocol control messages are sent on.</summary>
        public const int ChunkStreamId = 2;

        /// <summary>The largest chunk size the 31-bit Set Chunk Size field can carry.</summary>
        public const int MaxChunkSize = int.MaxValue;

        /// <summary>Set Chunk Size: from the next chunk on, the sender splits messages into chunks of at most <paramref name="chunkSize"/> bytes.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkSize"/> is less than 1.</exception>
        public static RtmpMessage CreateSetChunkSize(int chunkSize)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
            return Create(RtmpMessageType.SetChunkSize, (uint)chunkSize);
        }

        /// <summary>Abort: the receiver discards the partially received message on <paramref name="chunkStreamId"/>.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkStreamId"/> is outside 2..<see cref="RtmpChunkWriter.MaxChunkStreamId"/>.</exception>
        public static RtmpMessage CreateAbort(int chunkStreamId)
        {
            RtmpChunkWriter.ValidateChunkStreamId(chunkStreamId);
            return Create(RtmpMessageType.Abort, (uint)chunkStreamId);
        }

        /// <summary>Acknowledgement: <paramref name="sequenceNumber"/> bytes received so far (see <see cref="RtmpChunkReader.TryTakeAcknowledgement"/>).</summary>
        public static RtmpMessage CreateAcknowledgement(uint sequenceNumber) => Create(RtmpMessageType.Acknowledgement, sequenceNumber);

        /// <summary>Window Acknowledgement Size: the peer must acknowledge every <paramref name="windowSize"/> bytes it receives.</summary>
        public static RtmpMessage CreateWindowAcknowledgementSize(uint windowSize) => Create(RtmpMessageType.WindowAcknowledgementSize, windowSize);

        /// <summary>Set Peer Bandwidth: limits the peer's output to <paramref name="windowSize"/> unacknowledged bytes.</summary>
        public static RtmpMessage CreateSetPeerBandwidth(uint windowSize, RtmpPeerBandwidthLimitType limitType)
        {
            var payload = new byte[5];
            BinaryPrimitives.WriteUInt32BigEndian(payload, windowSize);
            payload[4] = (byte)limitType;
            return new RtmpMessage(RtmpMessageType.SetPeerBandwidth, 0, 0, payload);
        }

        /// <summary>Reads a Set Chunk Size payload: a 31-bit size of at least 1 (the top bit must be 0).</summary>
        public static int ReadSetChunkSize(ReadOnlySpan<byte> payload)
        {
            var value = ReadUInt32(payload, "Set Chunk Size", 4);
            if (value == 0 || value > MaxChunkSize)
            {
                throw new RtmpProtocolException($"RTMP: Set Chunk Size {value} is outside 1..{MaxChunkSize}");
            }

            return (int)value;
        }

        /// <summary>Reads an Abort payload: the chunk stream id whose partial message is discarded.</summary>
        public static int ReadAbort(ReadOnlySpan<byte> payload)
        {
            var value = ReadUInt32(payload, "Abort", 4);
            if (value < 2 || value > RtmpChunkWriter.MaxChunkStreamId)
            {
                throw new RtmpProtocolException($"RTMP: Abort names chunk stream {value}, outside 2..{RtmpChunkWriter.MaxChunkStreamId}");
            }

            return (int)value;
        }

        /// <summary>Reads an Acknowledgement payload: the sequence number (bytes the peer has received).</summary>
        public static uint ReadAcknowledgement(ReadOnlySpan<byte> payload) => ReadUInt32(payload, "Acknowledgement", 4);

        /// <summary>Reads a Window Acknowledgement Size payload.</summary>
        public static uint ReadWindowAcknowledgementSize(ReadOnlySpan<byte> payload) => ReadUInt32(payload, "Window Acknowledgement Size", 4);

        /// <summary>Reads a Set Peer Bandwidth payload; an unknown limit type throws.</summary>
        public static (uint WindowSize, RtmpPeerBandwidthLimitType LimitType) ReadSetPeerBandwidth(ReadOnlySpan<byte> payload)
        {
            var windowSize = ReadUInt32(payload, "Set Peer Bandwidth", 5);
            var limitType = payload[4];
            if (limitType > (byte)RtmpPeerBandwidthLimitType.Dynamic)
            {
                throw new RtmpProtocolException($"RTMP: Set Peer Bandwidth has unknown limit type {limitType}");
            }

            return (windowSize, (RtmpPeerBandwidthLimitType)limitType);
        }

        private static RtmpMessage Create(RtmpMessageType type, uint value)
        {
            var payload = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(payload, value);
            return new RtmpMessage(type, 0, 0, payload);
        }

        private static uint ReadUInt32(ReadOnlySpan<byte> payload, string name, int expectedLength)
        {
            if (payload.Length != expectedLength)
            {
                throw new RtmpProtocolException($"RTMP: {name} payload is {payload.Length} bytes, expected {expectedLength}");
            }

            return BinaryPrimitives.ReadUInt32BigEndian(payload);
        }
    }
}
