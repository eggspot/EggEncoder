using System.Runtime.CompilerServices;
using EggEncoder.Media;

namespace EggEncoder.Containers.Flv
{
    /// <summary>
    /// Reads an FLV stream (a file, an HTTP response body, any readable <see cref="Stream"/>) as an
    /// async packet source, through <see cref="FlvDemuxer"/>. Needs no <see cref="Stream.CanSeek"/>.
    /// No real-time pacing: packets come as fast as the stream is read (pacing is
    /// docs/live-ingest-restream-plan.md item 8).
    /// </summary>
    public sealed class FlvFileSource : IAsyncDisposable, IDisposable
    {
        private const int ReadSize = 64 * 1024;

        private readonly Stream _source;
        private readonly bool _leaveOpen;
        private readonly FlvDemuxer _demuxer;
        // Track-set changes and packets in demux order, so Tracks only advances when the packets
        // that follow a change are yielded (a config change mid-read must not run ahead of them).
        private readonly Queue<(MediaTrackSet? Tracks, MediaPacket Packet)> _ready = new();
        private MediaTrackSet? _tracks;
        private bool _started;
        private bool _disposed;

        /// <param name="source">The FLV bytes, read from its current position to its end.</param>
        /// <param name="leaveOpen">Leave <paramref name="source"/> open when this source is disposed.</param>
        public FlvFileSource(Stream source, bool leaveOpen = false)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (!source.CanRead)
            {
                throw new ArgumentException("The source stream must be readable", nameof(source));
            }

            _source = source;
            _leaveOpen = leaveOpen;
            _demuxer = new FlvDemuxer(tracks => _ready.Enqueue((tracks, default)), packet => _ready.Enqueue((null, packet)));
        }

        /// <summary>
        /// The track set that describes the packet most recently yielded by <see cref="ReadPacketsAsync"/>:
        /// <c>null</c> until it is known, then replaced, just before the first packet that uses it,
        /// whenever a track appears or its configuration changes.
        /// </summary>
        public MediaTrackSet? Tracks => _tracks;

        /// <summary>The most recent <c>onMetaData</c>, or <c>null</c>.</summary>
        public FlvMetadata? Metadata => _demuxer.Metadata;

        /// <summary>Tags the demuxer skipped (see <see cref="FlvDemuxer"/>).</summary>
        public int SkippedTagCount => _demuxer.SkippedTagCount;

        /// <summary>Reads the stream to its end, yielding every packet in order. Can be enumerated once.</summary>
        /// <exception cref="InvalidDataException">The stream is malformed.</exception>
        public async IAsyncEnumerable<MediaPacket> ReadPacketsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                throw new InvalidOperationException("ReadPacketsAsync can only be enumerated once");
            }

            _started = true;
            var buffer = new byte[ReadSize];
            while (true)
            {
                var read = await _source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    _demuxer.Complete();
                }
                else
                {
                    _demuxer.Feed(buffer.AsSpan(0, read));
                }

                while (_ready.TryDequeue(out var entry))
                {
                    if (entry.Tracks is not null)
                    {
                        _tracks = entry.Tracks;
                        continue;
                    }

                    yield return entry.Packet;
                }

                if (read == 0)
                {
                    yield break;
                }
            }
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
