using System.Buffers.Binary;
using EggEncoder.Codecs.Aac;
using EggEncoder.Live.Rtmp.Amf0;
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
            Completed
        }

        private readonly Action<MediaTrackSet> _onTracks;
        private readonly Action<MediaPacket> _onPacket;
        private readonly List<MediaPacket> _pending = [];
        private long _pendingBytes;

        private byte[] _buffer = new byte[4096];
        private int _bufferStart;
        private int _bufferEnd;
        private State _state = State.FileHeader;
        private long _lastTagSize;

        private bool _expectAudio;
        private bool _expectVideo;
        private MediaTrackInfo? _videoTrack;
        private MediaTrackInfo? _audioTrack;
        private bool _announced;

        private bool _hasTimestamp;
        private uint _lastRawTimestamp;
        private long _lastTimestamp;

        /// <param name="onTracks">Called with the full track set when it is first known and every time it changes.</param>
        /// <param name="onPacket">Called with each packet, in stream order, after the track set that describes it.</param>
        public FlvDemuxer(Action<MediaTrackSet> onTracks, Action<MediaPacket> onPacket)
        {
            ArgumentNullException.ThrowIfNull(onTracks);
            ArgumentNullException.ThrowIfNull(onPacket);
            _onTracks = onTracks;
            _onPacket = onPacket;
        }

        /// <summary>The current track set; <c>null</c> until it has been announced.</summary>
        public MediaTrackSet? Tracks { get; private set; }

        /// <summary>The most recent <c>onMetaData</c>, or <c>null</c> if none has been read.</summary>
        public FlvMetadata? Metadata { get; private set; }

        /// <summary>Whether the FLV header declared an audio track; valid once the header has been read.</summary>
        public bool HeaderDeclaresAudio => _expectAudio;

        /// <summary>Whether the FLV header declared a video track; valid once the header has been read.</summary>
        public bool HeaderDeclaresVideo => _expectVideo;

        /// <summary>Tags skipped because they carried nothing usable (see the remarks).</summary>
        public int SkippedTagCount { get; private set; }

        /// <summary>Parses as much of the stream as these bytes complete; the rest is kept for the next call.</summary>
        /// <exception cref="InvalidDataException">The stream is malformed.</exception>
        /// <exception cref="InvalidOperationException"><see cref="Complete"/> was already called.</exception>
        public void Feed(ReadOnlySpan<byte> data)
        {
            if (_state == State.Completed)
            {
                throw new InvalidOperationException("The demuxer has already completed");
            }

            Append(data);
            while (TryParseNext())
            {
            }

            Compact();
        }

        /// <summary>
        /// Signals the end of the input: announces the tracks if that hasn't happened yet and delivers
        /// every held packet.
        /// </summary>
        /// <exception cref="InvalidDataException">The input ended inside the file header or a tag.</exception>
        public void Complete()
        {
            if (_state == State.Completed)
            {
                return;
            }

            var leftover = _bufferEnd - _bufferStart;
            if (_state == State.FileHeader || leftover > 0)
            {
                throw new InvalidDataException($"FLV: input ended with an incomplete {(_state == State.FileHeader ? "file header" : "tag")} ({leftover} bytes left over)");
            }

            _state = State.Completed;
            Announce();
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

            _expectAudio = (data[4] & HeaderFlagAudio) != 0;
            _expectVideo = (data[4] & HeaderFlagVideo) != 0;
            var dataOffset = BinaryPrimitives.ReadUInt32BigEndian(data[5..]);
            if (dataOffset < FileHeaderSize || dataOffset > MaxDataOffset)
            {
                throw new InvalidDataException($"FLV: header DataOffset {dataOffset} is outside {FileHeaderSize}..{MaxDataOffset}");
            }

            return (int)dataOffset;
        }

        private void ParseTag(ReadOnlySpan<byte> tag)
        {
            var filtered = (tag[0] & 0x20) != 0;
            var tagType = tag[0] & 0x1F;
            var rawTimestamp = (uint)(tag[7] << 24 | tag[4] << 16 | tag[5] << 8 | tag[6]);
            var body = tag[TagHeaderSize..];
            if (filtered)
            {
                SkippedTagCount++;
                return;
            }

            switch (tagType)
            {
                case (int)FlvTagType.Audio:
                case (int)FlvTagType.Video:
                    var timestamp = Unwrap(rawTimestamp);
                    var parsed = tagType == (int)FlvTagType.Audio ? FlvTagBodyParser.ParseAudio(body) : FlvTagBodyParser.ParseVideo(body);
                    HandleMediaTag(parsed, timestamp);
                    break;
                case (int)FlvTagType.ScriptData:
                    ParseScriptData(body);
                    break;
                default:
                    SkippedTagCount++;
                    break;
            }
        }

        private long Unwrap(uint rawTimestamp)
        {
            if (!_hasTimestamp)
            {
                _hasTimestamp = true;
                _lastRawTimestamp = rawTimestamp;
                _lastTimestamp = rawTimestamp;
                return _lastTimestamp;
            }

            // The wrapping signed difference: a jump across 2^32 reads as a small step forward, and a
            // small step backward (audio/video interleaving) stays a small step backward.
            _lastTimestamp += unchecked((int)(rawTimestamp - _lastRawTimestamp));
            _lastRawTimestamp = rawTimestamp;
            return _lastTimestamp;
        }

        private void HandleMediaTag(FlvTagBody parsed, long timestamp)
        {
            switch (parsed.PacketType)
            {
                case FlvPacketType.SequenceHeader:
                    UpdateTrack(BuildTrack(parsed));
                    break;
                case FlvPacketType.CodedFrame:
                    var track = parsed.Kind == MediaKind.Video ? _videoTrack : _audioTrack;
                    if (track is null && parsed.Codec == MediaCodec.Mp3)
                    {
                        track = BuildMp3Track(parsed);
                        UpdateTrack(track);
                    }

                    if (track is null || track.Codec != parsed.Codec)
                    {
                        SkippedTagCount++;
                        return;
                    }

                    var packet = new MediaPacket(
                        track.TrackId,
                        timestamp + parsed.CompositionTime,
                        timestamp,
                        parsed.IsKeyframe,
                        track.ConfigVersion,
                        parsed.Payload.ToArray());
                    Deliver(packet);
                    break;
                case FlvPacketType.SequenceEnd:
                case FlvPacketType.Ignored:
                    break;
                default:
                    SkippedTagCount++;
                    break;
            }
        }

        private void UpdateTrack(MediaTrackInfo candidate)
        {
            var current = candidate.Kind == MediaKind.Video ? _videoTrack : _audioTrack;
            if (current is not null)
            {
                if (current.Codec == candidate.Codec && current.CodecConfiguration.Span.SequenceEqual(candidate.CodecConfiguration.Span))
                {
                    return;
                }

                candidate = candidate with { ConfigVersion = current.ConfigVersion + 1 };
            }

            if (candidate.Kind == MediaKind.Video)
            {
                _videoTrack = candidate;
            }
            else
            {
                _audioTrack = candidate;
            }

            if (_announced)
            {
                PublishTracks();
            }
            else if ((!_expectVideo || _videoTrack is not null) && (!_expectAudio || _audioTrack is not null))
            {
                Announce();
            }
        }

        private void Deliver(MediaPacket packet)
        {
            if (_announced)
            {
                _onPacket(packet);
                return;
            }

            _pending.Add(packet);
            _pendingBytes += packet.Payload.Length;
            if (_pending.Count >= MaxPendingPackets || _pendingBytes >= MaxPendingBytes)
            {
                Announce();
            }
        }

        private void Announce()
        {
            if (_announced || (_videoTrack is null && _audioTrack is null))
            {
                return;
            }

            _announced = true;
            PublishTracks();
            foreach (var packet in _pending)
            {
                _onPacket(packet);
            }

            _pending.Clear();
            _pendingBytes = 0;
        }

        private void PublishTracks()
        {
            var tracks = new List<MediaTrackInfo>(2);
            if (_videoTrack is not null)
            {
                tracks.Add(_videoTrack);
            }

            if (_audioTrack is not null)
            {
                tracks.Add(_audioTrack);
            }

            Tracks = new MediaTrackSet(tracks);
            _onTracks(Tracks);
        }

        private static MediaTrackInfo BuildTrack(FlvTagBody parsed)
        {
            var configuration = parsed.Payload.ToArray();
            switch (parsed.Codec)
            {
                case MediaCodec.H264:
                    var avc = AvcDecoderConfigurationRecord.Parse(configuration);
                    var (avcWidth, avcHeight) = TryReadDimensions(() =>
                    {
                        var sps = H264SpsParser.Parse(avc.SequenceParameterSets[0].Span);
                        return (sps.Width, sps.Height);
                    });
                    return new MediaTrackInfo(VideoTrackId, MediaKind.Video, MediaCodec.H264, Timescale, configuration, 0, width: avcWidth, height: avcHeight);

                case MediaCodec.H265:
                    var hevc = HevcDecoderConfigurationRecord.Parse(configuration);
                    // Parse rejects a record with no SPS, so there is always a first one.
                    var spsUnits = hevc.GetNalUnits(HevcDecoderConfigurationRecord.SpsNalUnitType);
                    var (hevcWidth, hevcHeight) = TryReadDimensions(() =>
                    {
                        var sps = H265SpsParser.Parse(spsUnits[0].Span);
                        return (sps.Width, sps.Height);
                    });
                    return new MediaTrackInfo(VideoTrackId, MediaKind.Video, MediaCodec.H265, Timescale, configuration, 0, width: hevcWidth, height: hevcHeight);

                case MediaCodec.Av1:
                case MediaCodec.Vp9:
                    if (configuration.Length == 0)
                    {
                        throw new InvalidDataException($"FLV: empty {parsed.Codec} sequence header");
                    }

                    return new MediaTrackInfo(VideoTrackId, MediaKind.Video, parsed.Codec.Value, Timescale, configuration, 0);

                default:
                    var (sampleRate, channels) = ReadAudioSpecificConfig(configuration);
                    return new MediaTrackInfo(AudioTrackId, MediaKind.Audio, MediaCodec.Aac, Timescale, configuration, 0, sampleRate, channels);
            }
        }

        // The SPS is only read for display dimensions; a variant the header parser can't read leaves
        // them unknown rather than rejecting a stream whose packets remux fine without them.
        private static (int Width, int Height) TryReadDimensions(Func<(int, int)> read)
        {
            try
            {
                return read();
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException)
            {
                return (0, 0);
            }
        }

        // AudioSpecificConfig (ISO/IEC 14496-3 1.6.2.1): audioObjectType (5 bits, 31 escapes to 6
        // more), samplingFrequencyIndex (4 bits, 15 escapes to an explicit 24-bit rate),
        // channelConfiguration (4 bits).
        internal static (int SampleRate, int Channels) ReadAudioSpecificConfig(ReadOnlySpan<byte> config)
        {
            var reader = new Transform.BitReader(config.ToArray());
            try
            {
                var objectType = (int)reader.ReadBits(5);
                if (objectType == 31)
                {
                    reader.ReadBits(6);
                }

                var frequencyIndex = (int)reader.ReadBits(4);
                int sampleRate;
                if (frequencyIndex == 15)
                {
                    sampleRate = (int)reader.ReadBits(24);
                }
                else if (frequencyIndex < AacTables.SampleRates.Length)
                {
                    sampleRate = AacTables.SampleRates[frequencyIndex];
                }
                else
                {
                    throw new InvalidDataException($"FLV: AudioSpecificConfig has reserved samplingFrequencyIndex {frequencyIndex}");
                }

                var channels = (int)reader.ReadBits(4);
                return (sampleRate, channels);
            }
            catch (EndOfStreamException e)
            {
                throw new InvalidDataException("FLV: truncated AAC AudioSpecificConfig", e);
            }
        }

        private static MediaTrackInfo BuildMp3Track(FlvTagBody parsed)
        {
            var (sampleRate, channels) = TryReadMpegAudioHeader(parsed.Payload) ?? (parsed.SoundRate, parsed.SoundChannels);
            return new MediaTrackInfo(AudioTrackId, MediaKind.Audio, MediaCodec.Mp3, Timescale, ReadOnlyMemory<byte>.Empty, 0, sampleRate, channels);
        }

        // The MPEG audio frame header gives the real sample rate; the FLV header's 2-bit rate field
        // can't say 48000 or 32000.
        private static (int SampleRate, int Channels)? TryReadMpegAudioHeader(ReadOnlySpan<byte> frame)
        {
            if (frame.Length < 4 || frame[0] != 0xFF || (frame[1] & 0xE0) != 0xE0)
            {
                return null;
            }

            var version = (frame[1] >> 3) & 0x03;
            var rateIndex = (frame[2] >> 2) & 0x03;
            if (version == 1 || rateIndex == 3)
            {
                return null;
            }

            var baseRate = rateIndex switch { 0 => 44100, 1 => 48000, _ => 32000 };
            var sampleRate = version switch { 3 => baseRate, 2 => baseRate / 2, _ => baseRate / 4 };
            var channels = ((frame[3] >> 6) & 0x03) == 3 ? 1 : 2;
            return (sampleRate, channels);
        }

        private void ParseScriptData(ReadOnlySpan<byte> body)
        {
            try
            {
                var reader = new Amf0Reader(body);
                var name = reader.ReadValue();
                if (name.String == "@setDataFrame" && !reader.AtEnd)
                {
                    name = reader.ReadValue();
                }

                if (name.String == "onMetaData" && !reader.AtEnd)
                {
                    var value = reader.ReadValue();
                    if (value.Type is Amf0Type.EcmaArray or Amf0Type.Object)
                    {
                        Metadata = new FlvMetadata(value.Properties);
                        return;
                    }
                }
            }
            catch (InvalidDataException)
            {
                // Metadata is descriptive only; an unreadable script tag is skipped, not fatal.
            }

            SkippedTagCount++;
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
