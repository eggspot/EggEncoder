namespace EggEncoder.Streaming
{
    /// <summary>
    /// I/O-agnostic destination for an HLS output: an init segment, a sequence of media segments,
    /// and a playlist that gets rewritten each time it changes. Implementations: <see cref="FileSystemStreamingSink"/>
    /// (a directory on disk), <see cref="InMemoryStreamingSink"/> (no filesystem at all, for tests
    /// or for a caller that wants bytes directly), and <see cref="DelegateStreamingSink"/> (a thin
    /// wrapper over caller-supplied delegates, for a destination this library doesn't know about --
    /// an HTTP PUT, an S3 upload). See docs/streaming-plan.md item 3a.
    /// </summary>
    public interface IStreamingSink
    {
        void WriteInitSegment(ReadOnlySpan<byte> bytes);

        void WriteMediaSegment(long sequenceNumber, ReadOnlySpan<byte> bytes);

        /// <summary>Called again, with the complete new content, every time the playlist changes.</summary>
        void WritePlaylist(ReadOnlySpan<byte> utf8Bytes);

        /// <summary>Called for a segment that has aged out of a live sliding window. Never called in VOD mode.</summary>
        void DeleteMediaSegment(long sequenceNumber);
    }
}
