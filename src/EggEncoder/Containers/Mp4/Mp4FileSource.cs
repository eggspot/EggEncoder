using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using EggEncoder.Codecs.Mov;
using EggEncoder.Media;

namespace EggEncoder.Containers.Mp4
{
    /// <summary>
    /// Reads a progressive (non-fragmented) MP4/MOV file as a packet source: every H.264, H.265, AAC
    /// and MP3 track becomes a <see cref="MediaTrackInfo"/>, every sample a <see cref="MediaPacket"/>
    /// with its real decode and presentation times (<c>stts</c>, <c>ctts</c>, then the first edit of
    /// <c>elst</c>, so B-frame delay and AAC priming show as negative timestamps, as ffmpeg reports
    /// them). Packets of all tracks come interleaved by decode time. Needs a seekable stream; the
    /// whole <c>moov</c> is read when the source is created. Not thread-safe.
    /// </summary>
    public sealed class Mp4FileSource : IAsyncDisposable, IDisposable
    {
        private readonly Stream _source;
        private readonly bool _leaveOpen;
        private readonly Mp4SourceTrack[] _tracks;
        private bool _disposed;

        /// <param name="source">A readable, seekable MP4/MOV stream.</param>
        /// <param name="leaveOpen">Leave <paramref name="source"/> open when this source is disposed.</param>
        /// <exception cref="ArgumentException">The stream can't be read or can't seek.</exception>
        /// <exception cref="InvalidDataException">The file is malformed.</exception>
        /// <exception cref="NotSupportedException">The file is a fragmented MP4.</exception>
        public Mp4FileSource(Stream source, bool leaveOpen = false)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (!source.CanRead || !source.CanSeek)
            {
                throw new ArgumentException("The source stream must be readable and seekable", nameof(source));
            }

            _source = source;
            _leaveOpen = leaveOpen;
            try
            {
                (_tracks, Duration, SkippedTrackCount) = ReadMovie(source);
            }
            catch (EndOfStreamException e)
            {
                throw new InvalidDataException("The MP4 file is truncated", e);
            }

            Tracks = new MediaTrackSet(_tracks.Select(t => t.Info).ToArray());
        }

        /// <summary>Opens a file for reading; the file is closed when the source is disposed.</summary>
        public static Mp4FileSource Open(string path)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                return new Mp4FileSource(stream);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        /// <summary>The tracks this source yields packets for, in file order. Ids are the file's own track ids.</summary>
        public MediaTrackSet Tracks { get; }

        /// <summary>The movie duration from <c>mvhd</c>, or the longest track when <c>mvhd</c> gives none.</summary>
        public TimeSpan Duration { get; }

        /// <summary>Tracks left out because their codec isn't one this source reads (or they carry no samples description it understands).</summary>
        public int SkippedTrackCount { get; }

        /// <summary>The number of samples of one track.</summary>
        /// <exception cref="ArgumentException">No track has this id.</exception>
        public int GetSampleCount(int trackId) => FindTrack(trackId).Offsets.Length;

        /// <summary>
        /// Every packet from <paramref name="startAt"/> on, interleaved by decode time (ties keep track
        /// order). Each track starts at its last random-access sample at or before
        /// <paramref name="startAt"/>; tracks other than video start one sample earlier still, so an
        /// audio decoder has its pre-roll. Can be enumerated any number of times.
        /// </summary>
        /// <exception cref="InvalidDataException">A sample lies past the end of the file.</exception>
        public IEnumerable<MediaPacket> ReadPackets(TimeSpan startAt = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ReadPacketsCore(startAt);
        }

        /// <inheritdoc cref="ReadPackets"/>
        public async IAsyncEnumerable<MediaPacket> ReadPacketsAsync(TimeSpan startAt = default, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var (track, index) in Schedule(startAt))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var payload = new byte[track.Sizes[index]];
                CheckSampleRange(track, index);
                _source.Position = track.Offsets[index];
                await _source.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
                yield return CreatePacket(track, index, payload);
            }
        }

        private IEnumerable<MediaPacket> ReadPacketsCore(TimeSpan startAt)
        {
            foreach (var (track, index) in Schedule(startAt))
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var payload = new byte[track.Sizes[index]];
                CheckSampleRange(track, index);
                _source.Position = track.Offsets[index];
                _source.ReadExactly(payload);
                yield return CreatePacket(track, index, payload);
            }
        }

        private void CheckSampleRange(Mp4SourceTrack track, int index)
        {
            if (track.Offsets[index] < 0 || track.Offsets[index] + track.Sizes[index] > _source.Length)
            {
                throw new InvalidDataException($"Sample {index} of track {track.Info.TrackId} lies past the end of the file");
            }
        }

        private static MediaPacket CreatePacket(Mp4SourceTrack track, int index, byte[] payload)
        {
            if (track.NalLengthSize is > 0 and < 4)
            {
                payload = WidenNalLengths(payload, track.NalLengthSize);
            }

            var dts = track.DecodeTimes[index] + track.EditShift;
            var pts = dts + (track.CompositionOffsets?[index] ?? 0);
            return new MediaPacket(track.Info.TrackId, pts, dts, track.IsSync(index), 0, payload);
        }

        private IEnumerable<(Mp4SourceTrack Track, int Index)> Schedule(TimeSpan startAt)
        {
            var cursors = new int[_tracks.Length];
            for (var t = 0; t < _tracks.Length; t++)
            {
                cursors[t] = _tracks[t].FindStartIndex(startAt);
            }

            while (true)
            {
                var best = -1;
                for (var t = 0; t < _tracks.Length; t++)
                {
                    if (cursors[t] >= _tracks[t].Offsets.Length)
                    {
                        continue;
                    }

                    if (best < 0 || IsEarlier(_tracks[t], cursors[t], _tracks[best], cursors[best]))
                    {
                        best = t;
                    }
                }

                if (best < 0)
                {
                    yield break;
                }

                yield return (_tracks[best], cursors[best]++);
            }
        }

        private static bool IsEarlier(Mp4SourceTrack a, int aIndex, Mp4SourceTrack b, int bIndex)
        {
            var aDts = (Int128)(a.DecodeTimes[aIndex] + a.EditShift) * b.Info.Timescale;
            var bDts = (Int128)(b.DecodeTimes[bIndex] + b.EditShift) * a.Info.Timescale;
            return aDts < bDts;
        }

        private Mp4SourceTrack FindTrack(int trackId) =>
            _tracks.FirstOrDefault(t => t.Info.TrackId == trackId)
                ?? throw new ArgumentException($"No track has id {trackId}", nameof(trackId));

        private static (Mp4SourceTrack[] Tracks, TimeSpan Duration, int Skipped) ReadMovie(Stream stream)
        {
            var moov = Mp4TrackReader.FindMoov(stream, "<stream>");
            if (Mp4TrackReader.IsFragmented(stream, moov))
            {
                throw new NotSupportedException("Fragmented MP4 ('mvex') is not supported; only progressive MP4/MOV files are");
            }

            var (movieTimescale, movieDuration) = Mp4TrackReader.ReadMovieHeader(stream, moov);
            var tracks = new List<Mp4SourceTrack>();
            var skipped = 0;
            var usedIds = new HashSet<int>();
            foreach (var header in Mp4TrackReader.ReadTrackHeaders(stream, moov))
            {
                var track = header.HandlerType is "vide" or "soun" && header.Timescale > 0 && usedIds.Add(header.TrackId)
                    ? Mp4SourceTrack.TryRead(stream, header, movieTimescale)
                    : null;
                if (track is null)
                {
                    skipped++;
                    continue;
                }

                tracks.Add(track);
            }

            var duration = movieTimescale > 0 && movieDuration > 0
                ? TimeSpan.FromSeconds(movieDuration / (double)movieTimescale)
                : tracks.Select(t => TimeSpan.FromSeconds(t.TotalDuration / (double)t.Info.Timescale)).DefaultIfEmpty(TimeSpan.Zero).Max();
            return (tracks.ToArray(), duration, skipped);
        }

        internal static byte[] WidenNalLengths(byte[] payload, int lengthSize)
        {
            var output = new List<byte>(payload.Length + 16);
            var position = 0;
            Span<byte> prefix = stackalloc byte[4];
            while (position < payload.Length)
            {
                if (position + lengthSize > payload.Length)
                {
                    throw new InvalidDataException("A NAL unit length prefix runs past the end of its sample");
                }

                var length = 0;
                for (var i = 0; i < lengthSize; i++)
                {
                    length = (length << 8) | payload[position + i];
                }

                position += lengthSize;
                if (position + length > payload.Length)
                {
                    throw new InvalidDataException("A NAL unit runs past the end of its sample");
                }

                BinaryPrimitives.WriteInt32BigEndian(prefix, length);
                output.AddRange(prefix.ToArray());
                output.AddRange(payload.AsSpan(position, length).ToArray());
                position += length;
            }

            return output.ToArray();
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
                _source.Dispose();
            }
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
