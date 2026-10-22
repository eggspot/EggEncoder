namespace EggEncoder.Media
{
    /// <summary>
    /// An output that consumes a packet stream: an RTMP push, an HLS segmenter, a file recording.
    /// Async because outputs sit at the network/file boundary; a <see cref="ValueTask"/> returned by
    /// <see cref="WriteAsync"/> that hasn't completed yet is this output's backpressure. Call
    /// <see cref="OpenAsync"/> once, then <see cref="WriteAsync"/> per packet, then
    /// <see cref="CompleteAsync"/> once; dispose to release resources whether or not the stream
    /// completed. See docs/live-ingest-restream-plan.md "API shape".
    /// </summary>
    public interface IMediaPacketSink : IAsyncDisposable
    {
        /// <summary>Starts the output for these tracks (writes headers, connects, and so on).</summary>
        ValueTask OpenAsync(MediaTrackSet tracks, CancellationToken cancellationToken = default);

        /// <summary>Writes one packet. Its <see cref="MediaPacket.TrackId"/> must belong to the set passed to <see cref="OpenAsync"/>.</summary>
        ValueTask WriteAsync(MediaPacket packet, CancellationToken cancellationToken = default);

        /// <summary>Signals the end of the stream: flushes, writes trailers, ends a playlist.</summary>
        ValueTask CompleteAsync(CancellationToken cancellationToken = default);
    }
}
