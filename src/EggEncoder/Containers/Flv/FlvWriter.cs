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
        private readonly Stream _destination;
        private readonly bool _leaveOpen;
        private readonly FlvMetadata? _metadata;
        private readonly FlvTagMuxer _muxer;
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
            _muxer = new FlvTagMuxer(WriteTag);
        }

        /// <summary>Writes the file header and <c>onMetaData</c> for these tracks.</summary>
        /// <exception cref="NotSupportedException">More than one audio or video track, a data track, or a codec FLV can't carry (Opus).</exception>
        /// <exception cref="ArgumentException">A track other than MP3 with no codec configuration, or metadata that can't be encoded as AMF0; nothing has been written.</exception>
        public void Open(MediaTrackSet tracks)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(tracks);
            if (_opened)
            {
                throw new InvalidOperationException("The writer is already open");
            }

            _muxer.SetTracks(tracks);
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
            WriteTag(FlvTagType.ScriptData, 0, metadataBody, ReadOnlySpan<byte>.Empty);
        }

        /// <summary>
        /// Replaces the track set after a configuration change or when a track appears late (as
        /// <see cref="FlvDemuxer"/> can report). A track already open keeps its kind; the set still
        /// holds at most one audio and one video track. The next packet of a track whose
        /// <see cref="MediaTrackInfo.ConfigVersion"/> changed, or that is new, is preceded by its
        /// sequence header. The file header's audio/video flags are not rewritten.
        /// </summary>
        public void UpdateTracks(MediaTrackSet tracks)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(tracks);
            ThrowIfNotWritable();
            _muxer.UpdateTracks(tracks);
        }

        /// <summary>Writes one packet, preceded by its track's sequence header when that hasn't been written for this configuration yet.</summary>
        /// <exception cref="ArgumentException">An unknown track, or a <see cref="MediaPacket.ConfigVersion"/> that isn't the track's current one.</exception>
        /// <exception cref="ArgumentOutOfRangeException">A negative DTS, or a composition offset outside FLV's signed 24 bits.</exception>
        public void Write(MediaPacket packet)
        {
            ThrowIfDisposed();
            ThrowIfNotWritable();
            _muxer.Write(packet);
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

        private void WriteTag(FlvTagType type, uint timestamp, ReadOnlySpan<byte> bodyHeader, ReadOnlySpan<byte> bodyPayload)
        {
            var dataSize = bodyHeader.Length + bodyPayload.Length;
            if (dataSize > 0xFFFFFF)
            {
                throw new ArgumentException($"A {dataSize}-byte tag body exceeds FLV's 24-bit DataSize", "packet");
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
