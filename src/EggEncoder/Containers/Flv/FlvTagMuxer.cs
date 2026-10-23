using System.Buffers.Binary;
using EggEncoder.Media;

namespace EggEncoder.Containers.Flv
{
    /// <summary>Receives one FLV tag body, split into the header this muxer built and the packet payload that follows it.</summary>
    internal delegate void FlvTagBodyCallback(FlvTagType type, uint timestamp, ReadOnlySpan<byte> bodyHeader, ReadOnlySpan<byte> bodyPayload);

    /// <summary>
    /// The tag-level half of <see cref="FlvWriter"/>: validates packets against the track set and turns
    /// each into an audio/video tag body, preceded by its track's sequence header whenever that
    /// configuration hasn't been sent yet. Shared by <see cref="FlvWriter"/> (which frames the bodies as
    /// tags in a file) and <c>Live.Rtmp.RtmpPublishClient</c> (which sends them as RTMP messages, whose
    /// bodies have the same layout), the write-side counterpart to <see cref="FlvTagDemuxer"/>.
    /// </summary>
    internal sealed class FlvTagMuxer
    {
        private const int MaxCompositionTime = (1 << 23) - 1;
        private const int MinCompositionTime = -(1 << 23);

        private const byte AacHeaderByteWithAlwaysSetRateSizeAndTypeBits = FlvTagBodyParser.SoundFormatAac << 4 | 3 << 2 | 1 << 1 | 1;

        private readonly FlvTagBodyCallback _onTag;
        private readonly Dictionary<int, MediaTrackInfo> _tracks = [];
        private readonly Dictionary<int, int> _writtenConfigVersions = [];

        public FlvTagMuxer(FlvTagBodyCallback onTag) => _onTag = onTag;

        /// <summary>Checks a track set FLV can carry and makes it current; nothing changes when it throws.</summary>
        /// <exception cref="NotSupportedException">More than one audio or video track, a data track, or a codec FLV can't carry.</exception>
        /// <exception cref="ArgumentException">A track other than MP3 with no codec configuration.</exception>
        public void SetTracks(MediaTrackSet tracks)
        {
            var audio = 0;
            var video = 0;
            foreach (var track in tracks.Tracks)
            {
                if (track.Codec != MediaCodec.Mp3 && track.Kind != MediaKind.Data && track.CodecConfiguration.IsEmpty)
                {
                    throw new ArgumentException($"Track {track.TrackId} ({track.Codec}) has no codec configuration", nameof(tracks));
                }

                switch (track.Kind)
                {
                    case MediaKind.Audio:
                        GetSoundFormat(track.Codec);
                        audio++;
                        break;
                    case MediaKind.Video:
                        GetVideoCodecId(track.Codec);
                        video++;
                        break;
                    default:
                        throw new NotSupportedException("FLV cannot carry a data track");
                }
            }

            if (audio > 1 || video > 1)
            {
                throw new NotSupportedException("FLV carries at most one audio and one video track");
            }

            _tracks.Clear();
            foreach (var track in tracks.Tracks)
            {
                _tracks[track.TrackId] = track;
            }
        }

        /// <summary>Like <see cref="SetTracks"/>, but a track already present must keep its kind.</summary>
        public void UpdateTracks(MediaTrackSet tracks)
        {
            foreach (var track in tracks.Tracks)
            {
                if (_tracks.TryGetValue(track.TrackId, out var existing) && existing.Kind != track.Kind)
                {
                    throw new ArgumentException($"Track {track.TrackId} was opened as a {existing.Kind} track, not {track.Kind}", nameof(tracks));
                }
            }

            SetTracks(tracks);
        }

        /// <summary>Emits one packet's tag body, preceded by its track's sequence header when that hasn't been emitted for this configuration yet.</summary>
        /// <exception cref="ArgumentException">An unknown track, or a <see cref="MediaPacket.ConfigVersion"/> that isn't the track's current one.</exception>
        /// <exception cref="ArgumentOutOfRangeException">A negative DTS, or a composition offset FLV can't carry.</exception>
        public void Write(MediaPacket packet)
        {
            if (!_tracks.TryGetValue(packet.TrackId, out var track))
            {
                throw new ArgumentException($"Packet track {packet.TrackId} is not in the open track set", nameof(packet));
            }

            if (packet.ConfigVersion != track.ConfigVersion)
            {
                throw new ArgumentException($"Packet config version {packet.ConfigVersion} is not track {track.TrackId}'s current version {track.ConfigVersion}; call UpdateTracks first", nameof(packet));
            }

            var dts = MediaTimestamp.Rescale(packet.Dts, track.Timescale, FlvDemuxer.Timescale);
            if (dts < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(packet), packet.Dts, "FLV timestamps must not be negative");
            }

            var compositionTime = MediaTimestamp.Rescale(packet.Pts, track.Timescale, FlvDemuxer.Timescale) - dts;
            if (compositionTime is < MinCompositionTime or > MaxCompositionTime)
            {
                throw new ArgumentOutOfRangeException(nameof(packet), compositionTime, "The composition offset does not fit FLV's signed 24-bit field");
            }

            if (track.Kind == MediaKind.Audio && compositionTime != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(packet), compositionTime, "An audio packet's PTS must equal its DTS");
            }

            if (track.Codec is MediaCodec.Av1 or MediaCodec.Vp9 && compositionTime != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(packet), compositionTime, $"FLV carries no composition time for {track.Codec}");
            }

            var timestamp = unchecked((uint)dts);
            if (!_writtenConfigVersions.TryGetValue(track.TrackId, out var written) || written != track.ConfigVersion)
            {
                WriteSequenceHeader(track, timestamp);
                _writtenConfigVersions[track.TrackId] = track.ConfigVersion;
            }

            if (track.Kind == MediaKind.Video)
            {
                WriteVideoFrame(track, packet, (int)compositionTime, timestamp);
            }
            else
            {
                WriteAudioFrame(track, packet.Payload.Span, timestamp);
            }
        }

        /// <summary>The legacy CodecID for H.264, and the FourCC (as a number, as ffmpeg writes it) for the Enhanced-FLV codecs: what <c>onMetaData</c>'s <c>videocodecid</c> carries.</summary>
        internal static int GetVideoCodecId(MediaCodec codec) => codec switch
        {
            MediaCodec.H264 => FlvTagBodyParser.VideoCodecAvc,
            MediaCodec.H265 => (int)FlvTagBodyParser.FourCcHvc1,
            MediaCodec.Av1 => (int)FlvTagBodyParser.FourCcAv01,
            MediaCodec.Vp9 => (int)FlvTagBodyParser.FourCcVp09,
            _ => throw new NotSupportedException($"FLV cannot carry {codec} video")
        };

        internal static int GetSoundFormat(MediaCodec codec) => codec switch
        {
            MediaCodec.Aac => FlvTagBodyParser.SoundFormatAac,
            MediaCodec.Mp3 => FlvTagBodyParser.SoundFormatMp3,
            _ => throw new NotSupportedException($"FLV cannot carry {codec} audio")
        };

        internal static bool IsEnhancedVideoCodec(MediaCodec codec) => codec is MediaCodec.H265 or MediaCodec.Av1 or MediaCodec.Vp9;

        private void WriteSequenceHeader(MediaTrackInfo track, uint timestamp)
        {
            switch (track.Codec)
            {
                case MediaCodec.Mp3:
                    return;
                case MediaCodec.Aac:
                    _onTag(FlvTagType.Audio, timestamp, [AacHeaderByteWithAlwaysSetRateSizeAndTypeBits, 0], track.CodecConfiguration.Span);
                    return;
                case MediaCodec.H264:
                    _onTag(FlvTagType.Video, timestamp, [FlvTagBodyParser.FrameTypeKeyframe << 4 | FlvTagBodyParser.VideoCodecAvc, 0, 0, 0, 0], track.CodecConfiguration.Span);
                    return;
                default:
                    Span<byte> header = stackalloc byte[5];
                    WriteExVideoHeader(header, FlvTagBodyParser.FrameTypeKeyframe, FlvTagBodyParser.ExPacketSequenceStart, track.Codec);
                    _onTag(FlvTagType.Video, timestamp, header, track.CodecConfiguration.Span);
                    return;
            }
        }

        private void WriteAudioFrame(MediaTrackInfo track, ReadOnlySpan<byte> payload, uint timestamp)
        {
            if (track.Codec == MediaCodec.Aac)
            {
                _onTag(FlvTagType.Audio, timestamp, [AacHeaderByteWithAlwaysSetRateSizeAndTypeBits, 1], payload);
                return;
            }

            var format = track.SampleRate == 8000 ? FlvTagBodyParser.SoundFormatMp3_8Khz : FlvTagBodyParser.SoundFormatMp3;
            var rateIndex = track.SampleRate switch { >= 44100 => 3, >= 22050 => 2, >= 11025 => 1, _ => 0 };
            var stereo = track.Channels == 1 ? 0 : 1;
            _onTag(FlvTagType.Audio, timestamp, [(byte)(format << 4 | rateIndex << 2 | 1 << 1 | stereo)], payload);
        }

        private void WriteVideoFrame(MediaTrackInfo track, MediaPacket packet, int compositionTime, uint timestamp)
        {
            var frameType = packet.IsKeyframe ? FlvTagBodyParser.FrameTypeKeyframe : FlvTagBodyParser.FrameTypeInterframe;
            Span<byte> header = stackalloc byte[8];
            int headerLength;
            if (track.Codec == MediaCodec.H264)
            {
                header[0] = (byte)(frameType << 4 | FlvTagBodyParser.VideoCodecAvc);
                header[1] = 1;
                WriteSignedInt24(header[2..], compositionTime);
                headerLength = 5;
            }
            else if (track.Codec == MediaCodec.H265 && compositionTime != 0)
            {
                WriteExVideoHeader(header, frameType, FlvTagBodyParser.ExPacketCodedFrames, track.Codec);
                WriteSignedInt24(header[5..], compositionTime);
                headerLength = 8;
            }
            else
            {
                var packetType = track.Codec == MediaCodec.H265 ? FlvTagBodyParser.ExPacketCodedFramesX : FlvTagBodyParser.ExPacketCodedFrames;
                WriteExVideoHeader(header, frameType, packetType, track.Codec);
                headerLength = 5;
            }

            _onTag(FlvTagType.Video, timestamp, header[..headerLength], packet.Payload.Span);
        }

        private static void WriteExVideoHeader(Span<byte> header, int frameType, int packetType, MediaCodec codec)
        {
            header[0] = (byte)(0x80 | frameType << 4 | packetType);
            BinaryPrimitives.WriteUInt32BigEndian(header[1..], (uint)GetVideoCodecId(codec));
        }

        private static void WriteSignedInt24(Span<byte> destination, int value)
        {
            destination[0] = (byte)(value >> 16);
            destination[1] = (byte)(value >> 8);
            destination[2] = (byte)value;
        }
    }
}
