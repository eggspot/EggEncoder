using System.Buffers;
using System.Buffers.Binary;

namespace EggEncoder.Live.Rtmp
{
    /// <summary>
    /// Splits RTMP messages into chunks (Adobe RTMP specification 1.0, section 5.3), the inverse of
    /// <see cref="RtmpChunkReader"/>. Each message is written whole, on the chunk stream id the caller
    /// picks (by convention <see cref="RtmpProtocolControl.ChunkStreamId"/> for protocol control and
    /// small ids from 3 up for commands, audio and video), with the most compact message header the
    /// previous message on that chunk stream allows. Pure and synchronous: it writes to an
    /// <see cref="IBufferWriter{T}"/> and never touches a socket. See docs/live-ingest-restream-plan.md item 3.
    /// </summary>
    /// <remarks>
    /// Header choice per chunk stream: type 0 for its first message, a different message stream id, or
    /// a timestamp that goes backwards; type 1 when the length or type changed; type 2 when only the
    /// timestamp delta is new; type 3 when even the delta repeats the previous type 1/2 delta. A type 3
    /// directly after a type 0 is never written, although the specification allows it (the type 0
    /// timestamp then serves as the delta): peers disagree on how to read it.
    /// Timestamps and deltas of 0xFFFFFF or more use the extended timestamp field, repeated in every
    /// continuation chunk of that message.
    /// </remarks>
    public sealed class RtmpChunkWriter
    {
        /// <summary>The highest chunk stream id the three-byte basic header can carry.</summary>
        public const int MaxChunkStreamId = 65599;

        private const uint ExtendedTimestampMarker = 0xFFFFFF;

        // Every timestamp delta below this is a step forwards; at or above it, the 32-bit timestamp
        // went backwards (or wrapped), and only an absolute type 0 header can say so.
        private const uint MaxForwardDelta = 0x7FFFFFFF;

        private sealed class ChunkStream
        {
            public uint Timestamp;
            public uint TimestampDelta;
            public bool LastHeaderHadDelta;
            public int Length;
            public RtmpMessageType Type;
            public uint StreamId;
        }

        private readonly Dictionary<int, ChunkStream> _chunkStreams = [];

        /// <summary>The chunk size this writer splits messages at: <see cref="RtmpChunkReader.DefaultChunkSize"/> until <see cref="WriteSetChunkSize"/> changes it.</summary>
        public int ChunkSize { get; private set; } = RtmpChunkReader.DefaultChunkSize;

        /// <summary>
        /// Writes a Set Chunk Size message (at the current chunk size, on <see cref="RtmpProtocolControl.ChunkStreamId"/>)
        /// and splits every later message at <paramref name="chunkSize"/>; the peer switches as it reads it.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkSize"/> is outside 1..<see cref="RtmpMessage.MaxPayloadLength"/>.</exception>
        public void WriteSetChunkSize(IBufferWriter<byte> destination, int chunkSize)
        {
            ArgumentNullException.ThrowIfNull(destination);
            ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(chunkSize, RtmpMessage.MaxPayloadLength);
            WriteMessage(destination, RtmpProtocolControl.ChunkStreamId, RtmpProtocolControl.CreateSetChunkSize(chunkSize));
            ChunkSize = chunkSize;
        }

        /// <summary>Writes <paramref name="message"/> as chunks on <paramref name="chunkStreamId"/>.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkStreamId"/> is outside 2..<see cref="MaxChunkStreamId"/>.</exception>
        /// <exception cref="ArgumentException">The payload is longer than <see cref="RtmpMessage.MaxPayloadLength"/>, or the message is a Set Chunk Size (use <see cref="WriteSetChunkSize"/>, so this writer switches too).</exception>
        public void Write(IBufferWriter<byte> destination, int chunkStreamId, RtmpMessage message)
        {
            ArgumentNullException.ThrowIfNull(destination);
            if (message.Type == RtmpMessageType.SetChunkSize)
            {
                throw new ArgumentException(
                    "Write a Set Chunk Size with WriteSetChunkSize, which also switches this writer to the new size.", nameof(message));
            }

            WriteMessage(destination, chunkStreamId, message);
        }

        private void WriteMessage(IBufferWriter<byte> destination, int chunkStreamId, RtmpMessage message)
        {
            ValidateChunkStreamId(chunkStreamId);
            var payload = message.Payload.Span;
            if (payload.Length > RtmpMessage.MaxPayloadLength)
            {
                throw new ArgumentException(
                    $"An RTMP message payload is at most {RtmpMessage.MaxPayloadLength} bytes; this one is {payload.Length}.", nameof(message));
            }

            _chunkStreams.TryGetValue(chunkStreamId, out var previous);
            var delta = previous is null ? 0 : unchecked(message.Timestamp - previous.Timestamp);
            int format;
            if (previous is null || previous.StreamId != message.StreamId || delta > MaxForwardDelta)
            {
                format = 0;
            }
            else if (previous.Length != payload.Length || previous.Type != message.Type)
            {
                format = 1;
            }
            else if (!previous.LastHeaderHadDelta || previous.TimestampDelta != delta)
            {
                format = 2;
            }
            else
            {
                format = 3;
            }

            var timestampValue = format == 0 ? message.Timestamp : delta;
            var extended = timestampValue >= ExtendedTimestampMarker;

            WriteChunkHeader(destination, format, chunkStreamId, extended, timestampValue, payload.Length, message);
            var offset = 0;
            while (true)
            {
                var pieceLength = Math.Min(ChunkSize, payload.Length - offset);
                destination.Write(payload.Slice(offset, pieceLength));
                offset += pieceLength;
                if (offset == payload.Length)
                {
                    break;
                }

                WriteChunkHeader(destination, 3, chunkStreamId, extended, timestampValue, payload.Length, message);
            }

            var state = previous ?? new ChunkStream();
            if (format != 3)
            {
                state.LastHeaderHadDelta = format != 0;
                state.TimestampDelta = format == 0 ? 0 : delta;
            }

            state.Timestamp = message.Timestamp;
            state.Length = payload.Length;
            state.Type = message.Type;
            state.StreamId = message.StreamId;
            _chunkStreams[chunkStreamId] = state;
        }

        internal static void ValidateChunkStreamId(int chunkStreamId)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(chunkStreamId, 2);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(chunkStreamId, MaxChunkStreamId);
        }

        private static void WriteChunkHeader(
            IBufferWriter<byte> destination, int format, int chunkStreamId, bool extended, uint timestampValue, int length, RtmpMessage message)
        {
            var span = destination.GetSpan(3 + 11 + 4);
            var position = 0;
            if (chunkStreamId < 64)
            {
                span[position++] = (byte)((format << 6) | chunkStreamId);
            }
            else if (chunkStreamId < 64 + 256)
            {
                span[position++] = (byte)(format << 6);
                span[position++] = (byte)(chunkStreamId - 64);
            }
            else
            {
                span[position++] = (byte)((format << 6) | 1);
                span[position++] = (byte)((chunkStreamId - 64) & 0xFF);
                span[position++] = (byte)((chunkStreamId - 64) >> 8);
            }

            if (format <= 2)
            {
                WriteUInt24(span[position..], extended ? ExtendedTimestampMarker : timestampValue);
                position += 3;
            }

            if (format <= 1)
            {
                WriteUInt24(span[position..], (uint)length);
                span[position + 3] = (byte)message.Type;
                position += 4;
            }

            if (format == 0)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(span[position..], message.StreamId);
                position += 4;
            }

            if (extended)
            {
                BinaryPrimitives.WriteUInt32BigEndian(span[position..], timestampValue);
                position += 4;
            }

            destination.Advance(position);
        }

        private static void WriteUInt24(Span<byte> destination, uint value)
        {
            destination[0] = (byte)(value >> 16);
            destination[1] = (byte)(value >> 8);
            destination[2] = (byte)value;
        }
    }
}
