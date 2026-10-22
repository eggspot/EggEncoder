using System.Buffers.Binary;

namespace EggEncoder.Live.Rtmp
{
    /// <summary>
    /// Reassembles RTMP messages from the chunk stream a peer sends after the handshake (Adobe RTMP
    /// specification 1.0, section 5.3): feed it bytes in pieces of any size, as they arrive from a
    /// socket, and it reports each complete message, in order of completion, through the callback
    /// given at construction. Messages on different chunk stream ids may interleave chunk by chunk.
    /// Pure and synchronous: it never touches a socket. See docs/live-ingest-restream-plan.md item 3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Protocol control the reader acts on itself, before reporting the message: Set Chunk Size
    /// (changes <see cref="ChunkSize"/>), Abort (drops the partial message on that chunk stream) and
    /// Window Acknowledgement Size (sets <see cref="AcknowledgementWindowSize"/>, after which
    /// <see cref="TryTakeAcknowledgement"/> says when an Acknowledgement is due). Every message,
    /// these included, is still reported; sending anything back is the caller's job.
    /// </para>
    /// <para>
    /// Timestamps: a type 0 header carries the absolute timestamp, type 1 and 2 a delta from the
    /// previous message on the same chunk stream, and a type 3 header that starts a new message
    /// repeats the previous delta (0 after a type 0 header). A value of 0xFFFFFF means a 32-bit
    /// extended timestamp follows; the continuation chunks of such a message repeat it, as the
    /// specification requires, but a continuation that omits it (some older publishers do) is also
    /// read correctly, by checking whether its next four bytes equal the extended value.
    /// </para>
    /// <para>
    /// Bounds (<see cref="RtmpChunkReaderOptions"/>): a chunk size, message length, chunk stream count
    /// or amount of partially received data over its limit, a malformed control message, a type 1-3
    /// header on a chunk stream that never had a type 0 one, and a new message header on a chunk
    /// stream whose previous message is unfinished all throw <see cref="RtmpProtocolException"/>,
    /// after which the reader is unusable.
    /// </para>
    /// </remarks>
    public sealed class RtmpChunkReader
    {
        /// <summary>The chunk size both directions start with, until a Set Chunk Size changes it.</summary>
        public const int DefaultChunkSize = 128;

        private const int ExtendedTimestampMarker = 0xFFFFFF;
        private const int InitialMessageBufferLength = 4096;

        // Input is appended and parsed in slices, so one huge Feed never grows the internal buffer past
        // this plus one chunk.
        private const int FeedSliceLength = 64 * 1024;

        private sealed class ChunkStream
        {
            public uint Timestamp;
            public uint TimestampDelta;
            public int Length;
            public RtmpMessageType Type;
            public uint StreamId;
            public bool HasExtendedTimestamp;
            public uint ExtendedTimestamp;

            // Non-null while a message is being assembled on this chunk stream.
            public byte[]? Buffer;
            public int Received;
        }

        private readonly Action<RtmpMessage> _onMessage;
        private readonly RtmpChunkReaderOptions _options;
        private readonly Dictionary<int, ChunkStream> _chunkStreams = [];

        private byte[] _buffer = new byte[4096];
        private int _bufferStart;
        private int _bufferEnd;
        private int _bufferedMessageBytes;
        private long _lastAcknowledged;
        private bool _faulted;

        /// <param name="onMessage">Called with each complete message, after any protocol control it carries has been applied.</param>
        /// <param name="options">The bounds to enforce; <c>null</c> uses the defaults.</param>
        public RtmpChunkReader(Action<RtmpMessage> onMessage, RtmpChunkReaderOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(onMessage);
            _onMessage = onMessage;
            _options = options ?? new RtmpChunkReaderOptions();
        }

        /// <summary>The peer's current chunk size: <see cref="DefaultChunkSize"/> until it sends Set Chunk Size.</summary>
        public int ChunkSize { get; private set; } = DefaultChunkSize;

        /// <summary>Every byte fed so far (the Acknowledgement sequence number counts these).</summary>
        public long BytesReceived { get; private set; }

        /// <summary>The window the peer set with Window Acknowledgement Size; 0 until it sends one.</summary>
        public uint AcknowledgementWindowSize { get; private set; }

        /// <summary>
        /// True when at least <see cref="AcknowledgementWindowSize"/> bytes have arrived since the last
        /// acknowledgement taken, giving the sequence number to send in an Acknowledgement
        /// (<see cref="RtmpProtocolControl.CreateAcknowledgement"/>); taking it starts the next window.
        /// </summary>
        public bool TryTakeAcknowledgement(out uint sequenceNumber)
        {
            if (AcknowledgementWindowSize == 0 || BytesReceived - _lastAcknowledged < AcknowledgementWindowSize)
            {
                sequenceNumber = 0;
                return false;
            }

            _lastAcknowledged = BytesReceived;
            sequenceNumber = unchecked((uint)BytesReceived);
            return true;
        }

        /// <summary>Parses every chunk these bytes complete; a partial chunk is kept for the next call.</summary>
        /// <exception cref="RtmpProtocolException">The peer broke the protocol or a bound; the reader can't be used afterwards.</exception>
        /// <exception cref="InvalidOperationException">An earlier call threw (a protocol error, or a callback exception).</exception>
        public void Feed(ReadOnlySpan<byte> data)
        {
            if (_faulted)
            {
                throw new InvalidOperationException("The chunk reader faulted on an earlier call and can't be used.");
            }

            try
            {
                BytesReceived += data.Length;
                while (!data.IsEmpty)
                {
                    var slice = data[..Math.Min(data.Length, FeedSliceLength)];
                    Append(slice);
                    data = data[slice.Length..];
                    while (TryReadChunk())
                    {
                    }
                }
            }
            catch
            {
                // The buffered bytes no longer line up with chunk boundaries, so nothing after this
                // point can be trusted.
                _faulted = true;
                throw;
            }
        }

        private bool TryReadChunk()
        {
            var available = _buffer.AsSpan(_bufferStart, _bufferEnd - _bufferStart);
            if (available.IsEmpty)
            {
                return false;
            }

            // Basic header: 2-bit format, then a 6-bit chunk stream id, where 0 and 1 mean a one- or
            // two-byte id follows (64-319 and 64-65599).
            var format = available[0] >> 6;
            var chunkStreamId = available[0] & 0x3F;
            var position = 1;
            if (chunkStreamId == 0)
            {
                if (available.Length < 2)
                {
                    return false;
                }

                chunkStreamId = 64 + available[1];
                position = 2;
            }
            else if (chunkStreamId == 1)
            {
                if (available.Length < 3)
                {
                    return false;
                }

                chunkStreamId = 64 + available[1] + (available[2] << 8);
                position = 3;
            }

            var headerLength = format switch { 0 => 11, 1 => 7, 2 => 3, _ => 0 };
            if (available.Length < position + headerLength)
            {
                return false;
            }

            _chunkStreams.TryGetValue(chunkStreamId, out var stream);
            if (stream is null)
            {
                if (format != 0)
                {
                    throw new RtmpProtocolException(
                        $"RTMP chunk: a type {format} header on chunk stream {chunkStreamId}, which never had a type 0 header");
                }

                if (_chunkStreams.Count >= _options.MaxChunkStreams)
                {
                    throw new RtmpProtocolException($"RTMP chunk: more than {_options.MaxChunkStreams} chunk streams on one connection");
                }
            }

            var isContinuation = stream?.Buffer is not null;
            if (isContinuation && format != 3)
            {
                throw new RtmpProtocolException(
                    $"RTMP chunk: a type {format} header on chunk stream {chunkStreamId} before its previous message was complete");
            }

            // Message header fields; whatever a format leaves out comes from the previous message.
            var header = available.Slice(position, headerLength);
            var timestampField = format <= 2 ? ReadUInt24(header) : 0u;
            var length = format <= 1 ? (int)ReadUInt24(header[3..]) : stream!.Length;
            var type = format <= 1 ? (RtmpMessageType)header[6] : stream!.Type;
            var streamId = format == 0 ? BinaryPrimitives.ReadUInt32LittleEndian(header[7..]) : stream!.StreamId;
            position += headerLength;

            var hasExtendedTimestamp = format <= 2 ? timestampField == ExtendedTimestampMarker : stream!.HasExtendedTimestamp;
            var extendedTimestamp = 0u;
            if (hasExtendedTimestamp)
            {
                if (available.Length < position + 4)
                {
                    return false;
                }

                extendedTimestamp = BinaryPrimitives.ReadUInt32BigEndian(available[position..]);
                if (!isContinuation || extendedTimestamp == stream!.ExtendedTimestamp)
                {
                    position += 4;
                }
            }

            if (!isContinuation && length > _options.MaxMessageLength)
            {
                throw new RtmpProtocolException(
                    $"RTMP chunk: message length {length} on chunk stream {chunkStreamId} exceeds the {_options.MaxMessageLength}-byte limit");
            }

            var remaining = isContinuation ? stream!.Length - stream.Received : length;
            var pieceLength = Math.Min(remaining, ChunkSize);
            if (_bufferedMessageBytes + pieceLength > _options.MaxBufferedBytes)
            {
                throw new RtmpProtocolException(
                    $"RTMP chunk: more than {_options.MaxBufferedBytes} bytes of partially received messages");
            }

            if (available.Length < position + pieceLength)
            {
                return false;
            }

            // The whole chunk is here: only now is any state changed.
            if (stream is null)
            {
                stream = new ChunkStream();
                _chunkStreams.Add(chunkStreamId, stream);
            }

            if (!isContinuation)
            {
                StartMessage(stream, format, hasExtendedTimestamp ? extendedTimestamp : timestampField, length, type, streamId);
                if (format <= 2)
                {
                    stream.HasExtendedTimestamp = hasExtendedTimestamp;
                    stream.ExtendedTimestamp = extendedTimestamp;
                }

                stream.Buffer = new byte[Math.Min(length, Math.Max(pieceLength, InitialMessageBufferLength))];
                stream.Received = 0;
            }

            var piece = available.Slice(position, pieceLength);
            EnsureMessageCapacity(stream, stream.Received + pieceLength);
            piece.CopyTo(stream.Buffer.AsSpan(stream.Received));
            stream.Received += pieceLength;
            _bufferedMessageBytes += pieceLength;
            _bufferStart += position + pieceLength;

            if (stream.Received == stream.Length)
            {
                var message = new RtmpMessage(stream.Type, stream.StreamId, stream.Timestamp, stream.Buffer);
                stream.Buffer = null;
                _bufferedMessageBytes -= stream.Length;
                ApplyProtocolControl(message);
                _onMessage(message);
            }

            return true;
        }

        private static void StartMessage(ChunkStream stream, int format, uint timestampValue, int length, RtmpMessageType type, uint streamId)
        {
            switch (format)
            {
                case 0:
                    stream.Timestamp = timestampValue;
                    stream.TimestampDelta = 0;
                    stream.StreamId = streamId;
                    break;
                case 1:
                case 2:
                    stream.TimestampDelta = timestampValue;
                    stream.Timestamp = unchecked(stream.Timestamp + timestampValue);
                    break;
                default:
                    stream.Timestamp = unchecked(stream.Timestamp + stream.TimestampDelta);
                    break;
            }

            stream.Length = length;
            stream.Type = type;
        }

        private void ApplyProtocolControl(RtmpMessage message)
        {
            switch (message.Type)
            {
                case RtmpMessageType.SetChunkSize:
                    var chunkSize = RtmpProtocolControl.ReadSetChunkSize(message.Payload.Span);
                    if (chunkSize > _options.MaxInboundChunkSize)
                    {
                        throw new RtmpProtocolException(
                            $"RTMP: Set Chunk Size {chunkSize} exceeds the {_options.MaxInboundChunkSize}-byte limit");
                    }

                    ChunkSize = chunkSize;
                    break;
                case RtmpMessageType.Abort:
                    var abortedChunkStreamId = RtmpProtocolControl.ReadAbort(message.Payload.Span);
                    if (_chunkStreams.TryGetValue(abortedChunkStreamId, out var aborted) && aborted.Buffer is not null)
                    {
                        _bufferedMessageBytes -= aborted.Received;
                        aborted.Buffer = null;
                    }

                    break;
                case RtmpMessageType.WindowAcknowledgementSize:
                    AcknowledgementWindowSize = RtmpProtocolControl.ReadWindowAcknowledgementSize(message.Payload.Span);
                    break;
            }
        }

        private static void EnsureMessageCapacity(ChunkStream stream, int needed)
        {
            var buffer = stream.Buffer!;
            if (needed > buffer.Length)
            {
                Array.Resize(ref buffer, Math.Min(stream.Length, Math.Max(needed, buffer.Length * 2)));
                stream.Buffer = buffer;
            }
        }

        private static uint ReadUInt24(ReadOnlySpan<byte> data) => (uint)((data[0] << 16) | (data[1] << 8) | data[2]);

        private void Append(ReadOnlySpan<byte> data)
        {
            var pending = _bufferEnd - _bufferStart;
            if (_bufferEnd + data.Length > _buffer.Length)
            {
                var target = pending + data.Length > _buffer.Length
                    ? new byte[Math.Max(pending + data.Length, _buffer.Length * 2)]
                    : _buffer;
                _buffer.AsSpan(_bufferStart, pending).CopyTo(target);
                _buffer = target;
                _bufferStart = 0;
                _bufferEnd = pending;
            }

            data.CopyTo(_buffer.AsSpan(_bufferEnd));
            _bufferEnd += data.Length;
        }
    }
}
