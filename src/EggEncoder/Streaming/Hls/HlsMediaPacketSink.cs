using EggEncoder.Media;

namespace EggEncoder.Streaming.Hls
{
    /// <summary>
    /// An <see cref="IMediaPacketSink"/> that packages a packet stream as HLS: <see cref="OpenAsync"/>
    /// builds a packet-driven <see cref="HlsSegmenter"/> for the tracks it receives, each
    /// <see cref="WriteAsync"/> calls <see cref="HlsSegmenter.AddPacket"/>, and
    /// <see cref="CompleteAsync"/> calls <see cref="HlsSegmenter.Complete"/>. Every call runs
    /// synchronously on the caller's thread and returns a completed <see cref="ValueTask"/>; the
    /// <see cref="IStreamingSink"/> does the I/O. Disposing doesn't complete the playlist.
    /// </summary>
    public sealed class HlsMediaPacketSink : IMediaPacketSink
    {
        private readonly IStreamingSink _sink;
        private readonly int _targetDurationSeconds;
        private readonly SegmentFormat _segmentFormat;
        private readonly int? _liveWindowSize;
        private HlsSegmenter? _segmenter;
        private bool _disposed;

        /// <param name="sink">Receives the init segment, the media segments and the playlist.</param>
        /// <param name="targetDurationSeconds">The segmenter's target segment duration.</param>
        /// <param name="segmentFormat">The container of every media segment.</param>
        /// <param name="liveWindowSize">The live sliding window's segment count, or <c>null</c> for VOD (every segment stays listed).</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="targetDurationSeconds"/> is not positive, or <paramref name="liveWindowSize"/> is less than 1.</exception>
        public HlsMediaPacketSink(IStreamingSink sink, int targetDurationSeconds, SegmentFormat segmentFormat = SegmentFormat.Fmp4, int? liveWindowSize = null)
        {
            ArgumentNullException.ThrowIfNull(sink);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetDurationSeconds);
            if (liveWindowSize is < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(liveWindowSize), liveWindowSize, "Live window size must be at least 1.");
            }

            _sink = sink;
            _targetDurationSeconds = targetDurationSeconds;
            _segmentFormat = segmentFormat;
            _liveWindowSize = liveWindowSize;
        }

        /// <summary>The segmenter <see cref="OpenAsync"/> built, or <c>null</c> before it.</summary>
        public HlsSegmenter? Segmenter => _segmenter;

        /// <inheritdoc />
        /// <exception cref="InvalidOperationException">The sink is already open.</exception>
        public ValueTask OpenAsync(MediaTrackSet tracks, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_segmenter is not null)
            {
                throw new InvalidOperationException("The sink is already open.");
            }

            _segmenter = _liveWindowSize is { } windowSize
                ? new HlsSegmenter(tracks, _targetDurationSeconds, _sink, windowSize, _segmentFormat)
                : new HlsSegmenter(tracks, _targetDurationSeconds, _sink, _segmentFormat);
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        /// <exception cref="InvalidOperationException">The sink isn't open yet, or is already complete.</exception>
        /// <exception cref="ArgumentException">The packet is rejected, as <see cref="HlsSegmenter.AddPacket"/> describes.</exception>
        public ValueTask WriteAsync(MediaPacket packet, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenSegmenter().AddPacket(packet);
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        /// <exception cref="InvalidOperationException">The sink isn't open yet.</exception>
        public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenSegmenter().Complete();
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
        }

        private HlsSegmenter OpenSegmenter()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _segmenter ?? throw new InvalidOperationException("Call OpenAsync before writing to the sink.");
        }
    }
}
