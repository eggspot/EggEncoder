using System.Buffers.Binary;
using EggEncoder.Media;

namespace EggEncoder.Containers.Flv
{
    /// <summary>
    /// An incremental FLV demuxer (FLV specification v10.1 Annex E, plus Enhanced FLV): feed it bytes
    /// in chunks of any size (a file, an HTTP body, a network stream) and it reports the stream's tracks
    /// and its packets through the two callbacks given at construction. Video stays length-prefixed
    /// (AVCC/HVCC) exactly as FLV carries it, and AAC stays a raw access unit, so packets are remuxed,
    /// never decoded. See docs/live-ingest-restream-plan.md item 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Tracks: the video track is always <see cref="VideoTrackId"/> and the audio track
    /// <see cref="AudioTrackId"/>, timescale 1000 (FLV's milliseconds). The tracks callback fires once
    /// every track the FLV header declares has its configuration (an AVC/HEVC/AV1/VP9 or AAC sequence
    /// header, or the first MP3 frame), and again whenever a track appears or its configuration changes
    /// (a new sequence header whose bytes differ bumps <see cref="MediaTrackInfo.ConfigVersion"/>).
    /// Packets that arrive before then are held, at most <see cref="MaxPendingPackets"/> of them and
    /// <see cref="MaxPendingBytes"/> of payload; past either limit the tracks known so far are
    /// announced anyway.
    /// </para>
    /// <para>
    /// Timestamps: FLV's 32-bit millisecond tag timestamps are unwrapped into a continuous 64-bit
    /// timeline, so a stream longer than 49.7 days, or a writer that wraps, still counts up. A coded
    /// video frame's PTS is its DTS plus the signed composition time.
    /// </para>
    /// <para>
    /// Strictness: a bad signature or header, a <c>PreviousTagSize</c> that doesn't match the tag
    /// before it, a malformed sequence header and input that ends mid-tag all throw
    /// <see cref="InvalidDataException"/>. Tags that carry nothing usable (an unsupported codec, a
    /// coded frame before its track's sequence header, an encrypted tag, an unknown tag type, an
    /// unreadable or unrelated script tag) are skipped and counted in <see cref="SkippedTagCount"/>.
    /// A missing <c>PreviousTagSize</c> after the very last tag is tolerated.
    /// </para>
    /// </remarks>
    public sealed class FlvDemuxer
    {
        /// <summary>The <see cref="MediaTrackInfo.TrackId"/> of the video track.</summary>
        public const int VideoTrackId = 0;

        /// <summary>The <see cref="MediaTrackInfo.TrackId"/> of the audio track.</summary>
        public const int AudioTrackId = 1;

        /// <summary>FLV timestamps are milliseconds.</summary>
        public const int Timescale = 1000;

        /// <summary>The most packets held while waiting for every declared track's configuration.</summary>
        public const int MaxPendingPackets = 1024;

        /// <summary>The most payload bytes held while waiting for every declared track's configuration.</summary>
        public const int MaxPendingBytes = 32 * 1024 * 1024;

        internal const int FileHeaderSize = 9;
        internal const int TagHeaderSize = 11;
        internal const int PreviousTagSizeLength = 4;
        internal const byte HeaderFlagAudio = 0x04;
        internal const byte HeaderFlagVideo = 0x01;

        // The specification fixes DataOffset at 9; anything past this many bytes of header padding is
        // treated as corrupt rather than skipped blindly.
        private const int MaxDataOffset = FileHeaderSize + 1024;

        private enum State
        {
            FileHeader,
            PreviousTagSize,
            Tag,
            Completed,
            Faulted
        }

        private readonly FlvTagDemuxer _tags;

        private byte[] _buffer = new byte[4096];
        private int _bufferStart;
        private int _bufferEnd;
        private State _state = State.FileHeader;
        private long _lastTagSize;

        /// <param name="onTracks">Called with the full track set when it is first known and every time it changes.</param>
        /// <param name="onPacket">Called with each packet, in stream order, after the track set that describes it.</param>
        public FlvDemuxer(Action<MediaTrackSet> onTracks, Action<MediaPacket> onPacket)
        {
            ArgumentNullException.ThrowIfNull(onTracks);
            ArgumentNullException.ThrowIfNull(onPacket);
            _tags = new FlvTagDemuxer(onTracks, onPacket);
        }

        /// <summary>The current track set; <c>null</c> until it has been announced.</summary>
        public MediaTrackSet? Tracks => _tags.Tracks;

        /// <summary>The most recent <c>onMetaData</c>, or <c>null</c> if none has been read.</summary>
        public FlvMetadata? Metadata => _tags.Metadata;

        /// <summary>Whether the FLV header declared an audio track; valid once the header has been read.</summary>
        public bool HeaderDeclaresAudio => _tags.ExpectsAudio;

        /// <summary>Whether the FLV header declared a video track; valid once the header has been read.</summary>
        public bool HeaderDeclaresVideo => _tags.ExpectsVideo;

        /// <summary>Tags skipped because they carried nothing usable (see the remarks).</summary>
        public int SkippedTagCount => _tags.SkippedTagCount;

        /// <summary>Parses as much of the stream as these bytes complete; the rest is kept for the next call.</summary>
        /// <exception cref="InvalidDataException">The stream is malformed; the demuxer can't be used afterwards.</exception>
        /// <exception cref="InvalidOperationException"><see cref="Complete"/> was already called, or an earlier call threw (malformed input, or a callback exception).</exception>
        public void Feed(ReadOnlySpan<byte> data)
        {
            ThrowIfUnusable();
            Append(data);
            try
            {
                while (TryParseNext())
                {
                }
            }
            catch
            {
                // Malformed input, or a callback that threw mid-tag: the buffer no longer matches
                // what was delivered, so nothing after this point can be trusted.
                _state = State.Faulted;
                throw;
            }

            Compact();
        }

        /// <summary>
        /// Signals the end of the input: announces the tracks if that hasn't happened yet and delivers
        /// every held packet.
        /// </summary>
        /// <exception cref="InvalidDataException">The input ended inside the file header or a tag.</exception>
        /// <exception cref="InvalidOperationException">An earlier <see cref="Feed"/> threw.</exception>
        public void Complete()
        {
            if (_state == State.Completed)
            {
                return;
            }

            ThrowIfUnusable();
            var leftover = _bufferEnd - _bufferStart;
            if (_state == State.FileHeader || leftover > 0)
            {
                throw new InvalidDataException($"FLV: input ended with an incomplete {(_state == State.FileHeader ? "file header" : _state == State.PreviousTagSize ? "PreviousTagSize" : "tag")} ({leftover} bytes left over)");
            }

            _state = State.Completed;
            _tags.Complete();
        }

        private void ThrowIfUnusable()
        {
            if (_state == State.Completed)
            {
                throw new InvalidOperationException("The demuxer has already completed");
            }

            if (_state == State.Faulted)
            {
                throw new InvalidOperationException("The demuxer stopped at malformed input or a callback exception and can't continue");
            }
        }

        private bool TryParseNext()
        {
            var available = _bufferEnd - _bufferStart;
            var data = _buffer.AsSpan(_bufferStart, available);
            switch (_state)
            {
                case State.FileHeader:
                    if (available < FileHeaderSize)
                    {
                        return false;
                    }

                    var dataOffset = ParseFileHeader(data);
                    if (available < dataOffset)
                    {
                        return false;
                    }

                    Consume(dataOffset);
                    _state = State.PreviousTagSize;
                    return true;

                case State.PreviousTagSize:
                    if (available < PreviousTagSizeLength)
                    {
                        return false;
                    }

                    var previousTagSize = BinaryPrimitives.ReadUInt32BigEndian(data);
                    if (previousTagSize != _lastTagSize)
                    {
                        throw new InvalidDataException($"FLV: PreviousTagSize {previousTagSize} does not match the preceding tag's size {_lastTagSize}");
                    }

                    Consume(PreviousTagSizeLength);
                    _state = State.Tag;
                    return true;

                default: // State.Tag; Feed never runs once Completed.
                    if (available < TagHeaderSize)
                    {
                        return false;
                    }

                    var dataSize = data[1] << 16 | data[2] << 8 | data[3];
                    if (available < TagHeaderSize + dataSize)
                    {
                        EnsureCapacity(TagHeaderSize + dataSize);
                        return false;
                    }

                    ParseTag(data[..(TagHeaderSize + dataSize)]);
                    Consume(TagHeaderSize + dataSize);
                    _lastTagSize = TagHeaderSize + dataSize;
                    _state = State.PreviousTagSize;
                    return true;
            }
        }

        private int ParseFileHeader(ReadOnlySpan<byte> data)
        {
            if (data[0] != (byte)'F' || data[1] != (byte)'L' || data[2] != (byte)'V')
            {
                throw new InvalidDataException("FLV: missing 'FLV' signature");
            }

            if (data[3] != 1)
            {
                throw new InvalidDataException($"FLV: unsupported version {data[3]}");
            }

            _tags.ExpectTracks(audio: (data[4] & HeaderFlagAudio) != 0, video: (data[4] & HeaderFlagVideo) != 0);
            var dataOffset = BinaryPrimitives.ReadUInt32BigEndian(data[5..]);
            if (dataOffset < FileHeaderSize || dataOffset > MaxDataOffset)
            {
                throw new InvalidDataException($"FLV: header DataOffset {dataOffset} is outside {FileHeaderSize}..{MaxDataOffset}");
            }

            return (int)dataOffset;
        }

        private void ParseTag(ReadOnlySpan<byte> tag)
        {
            var rawTimestamp = (uint)(tag[7] << 24 | tag[4] << 16 | tag[5] << 8 | tag[6]);
            _tags.ProcessTag(tag[0] & 0x1F, (tag[0] & 0x20) != 0, rawTimestamp, tag[TagHeaderSize..]);
        }

        private void Append(ReadOnlySpan<byte> data)
        {
            EnsureCapacity(_bufferEnd - _bufferStart + data.Length);
            data.CopyTo(_buffer.AsSpan(_bufferEnd));
            _bufferEnd += data.Length;
        }

        private void EnsureCapacity(int needed)
        {
            if (_bufferStart + needed <= _buffer.Length)
            {
                return;
            }

            var target = _buffer;
            if (needed > _buffer.Length)
            {
                target = new byte[Math.Max(needed, _buffer.Length * 2)];
            }

            var length = _bufferEnd - _bufferStart;
            Buffer.BlockCopy(_buffer, _bufferStart, target, 0, length);
            _buffer = target;
            _bufferStart = 0;
            _bufferEnd = length;
        }

        private void Consume(int count) => _bufferStart += count;

        private void Compact()
        {
            if (_bufferStart == _bufferEnd)
            {
                _bufferStart = 0;
                _bufferEnd = 0;
            }
        }
    }
}
