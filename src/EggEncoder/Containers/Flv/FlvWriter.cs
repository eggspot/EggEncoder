using System.Buffers.Binary;
using EggEncoder.Live.Rtmp.Amf0;
using EggEncoder.Media;

namespace EggEncoder.Containers.Flv
{
    /// <summary>
    /// Writes a packet stream as an FLV file (FLV specification v10.1 Annex E, plus Enhanced FLV): the
    /// file header, an <c>onMetaData</c> script tag, a sequence header before each track's first frame
    /// and again whenever its <see cref="MediaTrackInfo.ConfigVersion"/> changes, then one tag per
    /// packet. H.264, AAC and MP3 use legacy tags; H.265, AV1 and VP9 use Enhanced-FLV FourCC tags
    /// (<c>hvc1</c>, <c>av01</c>, <c>vp09</c>), as ffmpeg 6.1+ and OBS 29.1+ write them. See
    /// docs/live-ingest-restream-plan.md item 2.
    /// </summary>
    /// <remarks>
    /// The synchronous <see cref="Open"/>/<see cref="Write"/>/<see cref="Complete"/> are the core; the
    /// <see cref="IMediaPacketSink"/> members call them. Writes go straight to the destination, which
    /// needs no <see cref="Stream.CanSeek"/>: the <c>onMetaData</c> duration is written as 0 and never
    /// patched, the convention for live FLV. At most one audio and one video track. Timestamps are
    /// rescaled to milliseconds; the DTS must not be negative, and the composition offset must fit in
    /// FLV's signed 24 bits. Past 2^32 ms the tag timestamp wraps, which <see cref="FlvDemuxer"/>
    /// unwraps.
    /// </remarks>
    public sealed class FlvWriter : IMediaPacketSink, IDisposable
    {
        private const int MaxCompositionTime = (1 << 23) - 1;
        private const int MinCompositionTime = -(1 << 23);

        // SoundFormat 10, and the rate/size/type bits the specification says AAC always sets
        // (44 kHz, 16-bit, stereo); the real values are in the AudioSpecificConfig.
        private const byte AacHeaderByte = FlvTagBodyParser.SoundFormatAac << 4 | 3 << 2 | 1 << 1 | 1;

        private readonly Stream _destination;
        private readonly bool _leaveOpen;
        private readonly FlvMetadata? _metadata;
        private readonly Dictionary<int, MediaTrackInfo> _tracks = [];
        private readonly Dictionary<int, int> _writtenConfigVersions = [];
        private byte[] _scratch = new byte[64 * 1024];
        private bool _opened;
        private bool _completed;
        private bool _disposed;

        /// <param name="destination">Where the FLV bytes go; written forward only.</param>
        /// <param name="leaveOpen">Leave <paramref name="destination"/> open when this writer is disposed.</param>
        /// <param name="metadata">The <c>onMetaData</c> to write; <c>null</c> writes <see cref="FlvMetadata.FromTracks"/>.</param>
        public FlvWriter(Stream destination, bool leaveOpen = false, FlvMetadata? metadata = null)
        {
            ArgumentNullException.ThrowIfNull(destination);
            if (!destination.CanWrite)
            {
                throw new ArgumentException("The destination stream must be writable", nameof(destination));
            }

            _destination = destination;
            _leaveOpen = leaveOpen;
            _metadata = metadata;
        }

        /// <summary>Writes the file header and <c>onMetaData</c> for these tracks.</summary>
        /// <exception cref="NotSupportedException">More than one audio or video track, a data track, or a codec FLV can't carry (Opus).</exception>
        /// <exception cref="ArgumentException">The metadata can't be encoded as AMF0; nothing has been written.</exception>
        public void Open(MediaTrackSet tracks)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(tracks);
            if (_opened)
            {
                throw new InvalidOperationException("The writer is already open");
            }

            SetTracks(tracks);
            // Encoded before anything is written, so invalid metadata leaves the destination untouched.
            var metadata = _metadata ?? FlvMetadata.FromTracks(tracks);
            var metadataBody = Amf0Writer.Encode(Amf0Value.FromString("onMetaData"), Amf0Value.FromEcmaArray(metadata.Properties));
            var flags = (byte)0;
            foreach (var track in tracks.Tracks)
            {
                flags |= track.Kind == MediaKind.Audio ? FlvDemuxer.HeaderFlagAudio : FlvDemuxer.HeaderFlagVideo;
            }

            Span<byte> header = [(byte)'F', (byte)'L', (byte)'V', 1, flags, 0, 0, 0, (byte)FlvDemuxer.FileHeaderSize, 0, 0, 0, 0];
            _destination.Write(header);
            _opened = true;
            WriteTag(FlvTagType.ScriptData, 0, metadataBody);
        }

        /// <summary>
        /// Replaces the track set after a configuration change, keeping the same track ids and kinds.
        /// The next packet of a track whose <see cref="MediaTrackInfo.ConfigVersion"/> changed is
        /// preceded by a new sequence header.
        /// </summary>
        public void UpdateTracks(MediaTrackSet tracks)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(tracks);
            ThrowIfNotWritable();
            foreach (var track in tracks.Tracks)
            {
                if (!_tracks.TryGetValue(track.TrackId, out var existing) || existing.Kind != track.Kind)
                {
                    throw new ArgumentException($"Track {track.TrackId} was not opened as a {track.Kind} track", nameof(tracks));
                }
            }

            SetTracks(tracks);
        }

        /// <summary>Writes one packet, preceded by its track's sequence header when that hasn't been written for this configuration yet.</summary>
        /// <exception cref="ArgumentException">An unknown track, or a <see cref="MediaPacket.ConfigVersion"/> that isn't the track's current one.</exception>
        /// <exception cref="ArgumentOutOfRangeException">A negative DTS, or a composition offset outside FLV's signed 24 bits.</exception>
        public void Write(MediaPacket packet)
        {
            ThrowIfDisposed();
            ThrowIfNotWritable();
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

        /// <summary>Ends the file and flushes the destination. Further writes throw.</summary>
        public void Complete()
        {
            ThrowIfDisposed();
            ThrowIfNotWritable();
            _completed = true;
            _destination.Flush();
        }

        /// <inheritdoc />
        public ValueTask OpenAsync(MediaTrackSet tracks, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Open(tracks);
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask WriteAsync(MediaPacket packet, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(packet);
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Complete();
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (!_leaveOpen)
            {
                _destination.Dispose();
            }
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        // The legacy CodecID for H.264, and the FourCC (as a number, as ffmpeg writes it) for the
        // Enhanced-FLV codecs: what onMetaData's videocodecid carries.
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

        private void SetTracks(MediaTrackSet tracks)
        {
            var audio = 0;
            var video = 0;
            foreach (var track in tracks.Tracks)
            {
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

        private void WriteSequenceHeader(MediaTrackInfo track, uint timestamp)
        {
            switch (track.Codec)
            {
                case MediaCodec.Mp3:
                    return; // MP3 has no configuration record.
                case MediaCodec.Aac:
                    WriteTag(FlvTagType.Audio, timestamp, [AacHeaderByte, 0], track.CodecConfiguration.Span);
                    return;
                case MediaCodec.H264:
                    WriteTag(FlvTagType.Video, timestamp, [FlvTagBodyParser.FrameTypeKeyframe << 4 | FlvTagBodyParser.VideoCodecAvc, 0, 0, 0, 0], track.CodecConfiguration.Span);
                    return;
                default:
                    Span<byte> header = stackalloc byte[5];
                    WriteExVideoHeader(header, FlvTagBodyParser.FrameTypeKeyframe, FlvTagBodyParser.ExPacketSequenceStart, track.Codec);
                    WriteTag(FlvTagType.Video, timestamp, header, track.CodecConfiguration.Span);
                    return;
            }
        }

        private void WriteAudioFrame(MediaTrackInfo track, ReadOnlySpan<byte> payload, uint timestamp)
        {
            if (track.Codec == MediaCodec.Aac)
            {
                WriteTag(FlvTagType.Audio, timestamp, [AacHeaderByte, 1], payload);
                return;
            }

            var format = track.SampleRate == 8000 ? FlvTagBodyParser.SoundFormatMp3_8Khz : FlvTagBodyParser.SoundFormatMp3;
            var rateIndex = track.SampleRate switch { >= 44100 => 3, >= 22050 => 2, >= 11025 => 1, _ => 0 };
            var stereo = track.Channels == 1 ? 0 : 1;
            WriteTag(FlvTagType.Audio, timestamp, [(byte)(format << 4 | rateIndex << 2 | 1 << 1 | stereo)], payload);
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
                // HEVC with no composition offset uses CodedFramesX (no offset field), as ffmpeg does;
                // AV1 and VP9 CodedFrames carry no offset field at all.
                var packetType = track.Codec == MediaCodec.H265 ? FlvTagBodyParser.ExPacketCodedFramesX : FlvTagBodyParser.ExPacketCodedFrames;
                WriteExVideoHeader(header, frameType, packetType, track.Codec);
                headerLength = 5;
            }

            WriteTag(FlvTagType.Video, timestamp, header[..headerLength], packet.Payload.Span);
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

        private void WriteTag(FlvTagType type, uint timestamp, ReadOnlySpan<byte> body) =>
            WriteTag(type, timestamp, body, ReadOnlySpan<byte>.Empty);

        private void WriteTag(FlvTagType type, uint timestamp, ReadOnlySpan<byte> bodyHeader, ReadOnlySpan<byte> bodyPayload)
        {
            var dataSize = bodyHeader.Length + bodyPayload.Length;
            if (dataSize > 0xFFFFFF)
            {
                throw new ArgumentException($"A {dataSize}-byte tag body exceeds FLV's 24-bit DataSize");
            }

            var total = FlvDemuxer.TagHeaderSize + dataSize + FlvDemuxer.PreviousTagSizeLength;
            if (_scratch.Length < total)
            {
                _scratch = new byte[Math.Max(total, _scratch.Length * 2)];
            }

            var tag = _scratch.AsSpan(0, total);
            tag[0] = (byte)type;
            tag[1] = (byte)(dataSize >> 16);
            tag[2] = (byte)(dataSize >> 8);
            tag[3] = (byte)dataSize;
            tag[4] = (byte)(timestamp >> 16);
            tag[5] = (byte)(timestamp >> 8);
            tag[6] = (byte)timestamp;
            tag[7] = (byte)(timestamp >> 24);
            tag[8] = 0;
            tag[9] = 0;
            tag[10] = 0;
            bodyHeader.CopyTo(tag[FlvDemuxer.TagHeaderSize..]);
            bodyPayload.CopyTo(tag[(FlvDemuxer.TagHeaderSize + bodyHeader.Length)..]);
            BinaryPrimitives.WriteUInt32BigEndian(tag[(FlvDemuxer.TagHeaderSize + dataSize)..], (uint)(FlvDemuxer.TagHeaderSize + dataSize));
            _destination.Write(tag);
        }

        private void ThrowIfNotWritable()
        {
            if (!_opened)
            {
                throw new InvalidOperationException("Open must be called first");
            }

            if (_completed)
            {
                throw new InvalidOperationException("The writer has already completed");
            }
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
