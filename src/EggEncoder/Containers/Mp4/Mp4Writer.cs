using EggEncoder.Codecs.Mov;
using EggEncoder.Media;
using EggEncoder.Streaming.Mp4;

namespace EggEncoder.Containers.Mp4
{
    /// <summary>
    /// Writes a packet stream as a progressive MP4 with <c>moov</c> before <c>mdat</c> (faststart),
    /// so the file plays while it downloads. H.264 (<c>avc1</c>), H.265 (<c>hvc1</c>) and AAC
    /// (<c>mp4a</c>) tracks, passed through without decoding. Sample payloads are kept in a scratch
    /// stream until <see cref="Complete"/>, which then writes the whole file to the destination in
    /// one forward pass, so the destination needs no <see cref="Stream.CanSeek"/>.
    /// </summary>
    /// <remarks>
    /// Durations come from the DTS deltas; a track's last sample repeats the previous duration.
    /// Composition offsets go into <c>ctts</c> (version 1 when one is negative). A track whose first
    /// PTS isn't 0 gets one <c>elst</c> edit: an empty edit for a late start, and a media time that
    /// hides B-frame delay and AAC priming (packets with negative PTS), so a reader sees the same
    /// timestamps back; the movie timescale is twice the finest track timescale, so edit times
    /// round-trip to the tick. Samples are grouped in chunks of at most half a second, interleaved by DTS.
    /// A video track's <see cref="MediaTrackInfo.Rotation"/> becomes the <c>tkhd</c> matrix. Files
    /// whose sample data reaches past 4 GiB are refused. A mid-stream configuration change is
    /// refused: every packet must carry its track's <see cref="MediaTrackInfo.ConfigVersion"/>.
    /// </remarks>
    public sealed class Mp4Writer : IMediaPacketSink, IDisposable
    {
        private const int MinimumMovieTimescale = 1000;
        private const double MaxChunkSeconds = 0.5;
        private const long MaxFileBytes = uint.MaxValue;

        private readonly Stream _destination;
        private readonly bool _leaveOpen;
        private readonly Stream _scratch;
        private readonly bool _ownsScratch;
        private Mp4WriterTrack[] _tracks = [];
        private long _payloadBytes;
        private bool _opened;
        private bool _completed;
        private bool _disposed;

        /// <param name="destination">Where the MP4 bytes go; written forward only, during <see cref="Complete"/>.</param>
        /// <param name="leaveOpen">Leave <paramref name="destination"/> open when this writer is disposed.</param>
        /// <param name="scratch">
        /// A readable, writable, seekable stream that holds sample payloads until <see cref="Complete"/>,
        /// written from its current position; not disposed by this writer. <c>null</c> uses a temporary
        /// file that is deleted when the writer is disposed.
        /// </param>
        public Mp4Writer(Stream destination, bool leaveOpen = false, Stream? scratch = null)
        {
            ArgumentNullException.ThrowIfNull(destination);
            if (!destination.CanWrite)
            {
                throw new ArgumentException("The destination stream must be writable", nameof(destination));
            }

            if (scratch is not null && (!scratch.CanRead || !scratch.CanWrite || !scratch.CanSeek))
            {
                throw new ArgumentException("The scratch stream must be readable, writable and seekable", nameof(scratch));
            }

            _destination = destination;
            _leaveOpen = leaveOpen;
            _ownsScratch = scratch is null;
            _scratch = scratch ?? new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.DeleteOnClose);
        }

        /// <summary>Starts the file for these tracks. Nothing is written to the destination until <see cref="Complete"/>.</summary>
        /// <exception cref="ArgumentException">An empty set, or a track whose codec configuration is missing or invalid.</exception>
        /// <exception cref="NotSupportedException">A codec other than H.264, H.265 or AAC.</exception>
        public void Open(MediaTrackSet tracks)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(tracks);
            if (_opened)
            {
                throw new InvalidOperationException("The writer is already open");
            }

            if (tracks.Tracks.Count == 0)
            {
                throw new ArgumentException("An MP4 file needs at least one track.", nameof(tracks));
            }

            var writerTracks = new Mp4WriterTrack[tracks.Tracks.Count];
            for (var i = 0; i < writerTracks.Length; i++)
            {
                writerTracks[i] = new Mp4WriterTrack(tracks.Tracks[i], FmpInitSegmentWriter.ToInitTrack(tracks.Tracks[i], i + 1));
            }

            _tracks = writerTracks;
            _opened = true;
        }

        /// <summary>Adds one packet; its payload is copied to the scratch stream.</summary>
        /// <exception cref="ArgumentException">An unknown track, another config version, a DTS not greater than the track's previous one, or a PTS - DTS outside 32 bits.</exception>
        /// <exception cref="NotSupportedException">The sample data would pass 4 GiB.</exception>
        public void Write(MediaPacket packet)
        {
            ThrowIfDisposed();
            ThrowIfNotWritable();
            var track = Array.Find(_tracks, t => t.Info.TrackId == packet.TrackId)
                ?? throw new ArgumentException($"Packet for track {packet.TrackId}, which isn't in this writer's track set.", nameof(packet));
            if (packet.ConfigVersion != track.Info.ConfigVersion)
            {
                throw new ArgumentException($"Packet for track {packet.TrackId} has config version {packet.ConfigVersion}; the track's is {track.Info.ConfigVersion}. An MP4 file can't change configuration mid-stream.", nameof(packet));
            }

            if (track.Samples.Count > 0 && packet.Dts <= track.Samples[^1].Dts)
            {
                throw new ArgumentException($"Packet for track {packet.TrackId} has DTS {packet.Dts}, not greater than the track's previous {track.Samples[^1].Dts}.", nameof(packet));
            }

            if (packet.CompositionOffset is < int.MinValue or > int.MaxValue)
            {
                throw new ArgumentException($"Packet for track {packet.TrackId} has a PTS - DTS ({packet.CompositionOffset}) outside the 32-bit range.", nameof(packet));
            }

            if (_payloadBytes + packet.Payload.Length > MaxFileBytes)
            {
                throw new NotSupportedException("MP4 files whose sample data passes 4 GiB are not supported.");
            }

            var scratchOffset = _scratch.Position;
            _scratch.Write(packet.Payload.Span);
            _payloadBytes += packet.Payload.Length;
            track.Samples.Add(new Mp4WriterSample(packet.Dts, (int)packet.CompositionOffset, packet.Payload.Length, packet.IsKeyframe, scratchOffset));
        }

        /// <summary>Writes the whole file to the destination and flushes it. Further writes throw.</summary>
        /// <exception cref="NotSupportedException">The file would pass 4 GiB.</exception>
        /// <exception cref="ArgumentException">A sample duration doesn't fit 32 bits.</exception>
        public void Complete()
        {
            ThrowIfDisposed();
            ThrowIfNotWritable();
            var layout = Mp4WriterLayout.Build(_tracks, MaxChunkSeconds);
            var ftyp = BuildFtyp();
            var movieTimescale = (int)Math.Clamp(2L * _tracks.Max(t => t.Info.Timescale), MinimumMovieTimescale, int.MaxValue);
            var moovLength = Mp4MoovBuilder.Build(_tracks, layout, movieTimescale, 0).Length;
            var mdatStart = ftyp.Length + moovLength + 8L;
            if (mdatStart + _payloadBytes > MaxFileBytes)
            {
                throw new NotSupportedException("MP4 files larger than 4 GiB are not supported.");
            }

            var moov = Mp4MoovBuilder.Build(_tracks, layout, movieTimescale, mdatStart);
            _completed = true;
            _destination.Write(ftyp);
            _destination.Write(moov);
            Span<byte> mdatHeader = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(mdatHeader, (uint)(8 + _payloadBytes));
            "mdat"u8.CopyTo(mdatHeader[4..]);
            _destination.Write(mdatHeader);
            CopyPayloads(layout);
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
            if (_ownsScratch)
            {
                _scratch.Dispose();
            }

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

        private void CopyPayloads(Mp4WriterLayout layout)
        {
            var buffer = new byte[64 * 1024];
            foreach (var chunk in layout.Chunks)
            {
                var samples = _tracks[chunk.TrackIndex].Samples;
                for (var i = chunk.FirstSample; i < chunk.FirstSample + chunk.SampleCount; i++)
                {
                    var sample = samples[i];
                    _scratch.Position = sample.ScratchOffset;
                    var remaining = sample.Size;
                    while (remaining > 0)
                    {
                        var read = _scratch.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                        if (read == 0)
                        {
                            throw new InvalidOperationException("The scratch stream ended before every sample was read back.");
                        }

                        _destination.Write(buffer, 0, read);
                        remaining -= read;
                    }
                }
            }
        }

        private byte[] BuildFtyp()
        {
            var builder = new Mp4BoxBuilder()
                .WriteFourCc("isom")
                .WriteUInt32(512)
                .WriteFourCc("isom")
                .WriteFourCc("iso2");
            if (_tracks.Any(t => t.Info.Codec == MediaCodec.H264))
            {
                builder.WriteFourCc("avc1");
            }

            builder.WriteFourCc("mp41");
            return new Mp4BoxBuilder().WriteBox("ftyp", builder.ToArray()).ToArray();
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
