using EggEncoder.Codecs.Aac;
using EggEncoder.Live.Rtmp.Amf0;
using EggEncoder.Media;

namespace EggEncoder.Containers.Flv
{
    /// <summary>
    /// The tag-level half of <see cref="FlvDemuxer"/>: turns one FLV tag at a time (its type, 32-bit
    /// millisecond timestamp and body) into tracks and packets. Shared, unchanged, by
    /// <see cref="FlvDemuxer"/> (tags framed by an FLV file) and the RTMP server (an RTMP audio, video
    /// or AMF0 data message is an FLV tag body with the same type id), so both deliver identical
    /// packets for the same tags. See <see cref="FlvDemuxer"/>'s remarks for the track, timestamp and
    /// skipping rules.
    /// </summary>
    internal sealed class FlvTagDemuxer
    {
        private readonly Action<MediaTrackSet> _onTracks;
        private readonly Action<MediaPacket> _onPacket;
        private readonly List<MediaPacket> _pending = [];
        private long _pendingBytes;

        private bool _expectAudio;
        private bool _expectVideo;
        private MediaTrackInfo? _videoTrack;
        private MediaTrackInfo? _audioTrack;
        private bool _announced;

        private bool _hasTimestamp;
        private uint _lastRawTimestamp;
        private long _lastTimestamp;

        public FlvTagDemuxer(Action<MediaTrackSet> onTracks, Action<MediaPacket> onPacket)
        {
            _onTracks = onTracks;
            _onPacket = onPacket;
        }

        public MediaTrackSet? Tracks { get; private set; }

        public FlvMetadata? Metadata { get; private set; }

        public bool ExpectsAudio => _expectAudio;

        public bool ExpectsVideo => _expectVideo;

        public int SkippedTagCount { get; private set; }

        /// <summary>
        /// Which tracks to wait for before the first announcement (an FLV file header's flags, or an
        /// RTMP publisher's <c>onMetaData</c>). Ignored once the tracks have been announced.
        /// </summary>
        public void ExpectTracks(bool audio, bool video)
        {
            if (_announced)
            {
                return;
            }

            _expectAudio = audio;
            _expectVideo = video;
            if ((_videoTrack is not null || _audioTrack is not null) && AllExpectedConfigured())
            {
                Announce();
            }
        }

        public void ProcessTag(int tagType, bool filtered, uint rawTimestamp, ReadOnlySpan<byte> body)
        {
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

        /// <summary>The input ended: announces the tracks if that hasn't happened yet and delivers every held packet.</summary>
        public void Complete() => Announce();

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

        private bool AllExpectedConfigured() => (!_expectVideo || _videoTrack is not null) && (!_expectAudio || _audioTrack is not null);

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
            else if (AllExpectedConfigured())
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
            if (_pending.Count >= FlvDemuxer.MaxPendingPackets || _pendingBytes >= FlvDemuxer.MaxPendingBytes)
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
                    return new MediaTrackInfo(FlvDemuxer.VideoTrackId, MediaKind.Video, MediaCodec.H264, FlvDemuxer.Timescale, configuration, 0, width: avcWidth, height: avcHeight);

                case MediaCodec.H265:
                    var hevc = HevcDecoderConfigurationRecord.Parse(configuration);
                    // Parse rejects a record with no SPS, so there is always a first one.
                    var spsUnits = hevc.GetNalUnits(HevcDecoderConfigurationRecord.SpsNalUnitType);
                    var (hevcWidth, hevcHeight) = TryReadDimensions(() =>
                    {
                        var sps = H265SpsParser.Parse(spsUnits[0].Span);
                        return (sps.Width, sps.Height);
                    });
                    return new MediaTrackInfo(FlvDemuxer.VideoTrackId, MediaKind.Video, MediaCodec.H265, FlvDemuxer.Timescale, configuration, 0, width: hevcWidth, height: hevcHeight);

                case MediaCodec.Av1:
                case MediaCodec.Vp9:
                    if (configuration.Length == 0)
                    {
                        throw new InvalidDataException($"FLV: empty {parsed.Codec} sequence header");
                    }

                    return new MediaTrackInfo(FlvDemuxer.VideoTrackId, MediaKind.Video, parsed.Codec.Value, FlvDemuxer.Timescale, configuration, 0);

                default:
                    var (sampleRate, channels) = ReadAudioSpecificConfig(configuration);
                    return new MediaTrackInfo(FlvDemuxer.AudioTrackId, MediaKind.Audio, MediaCodec.Aac, FlvDemuxer.Timescale, configuration, 0, sampleRate, channels);
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
            return new MediaTrackInfo(FlvDemuxer.AudioTrackId, MediaKind.Audio, MediaCodec.Mp3, FlvDemuxer.Timescale, ReadOnlyMemory<byte>.Empty, 0, sampleRate, channels);
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
    }
}
