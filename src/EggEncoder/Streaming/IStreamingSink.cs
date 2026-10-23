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

        /// <summary>
        /// Writes the init segment under <paramref name="fileName"/> and returns the URI the
        /// playlist's <c>EXT-X-MAP</c> lists for it. <see cref="Hls.HlsSegmenter"/> calls this
        /// overload; the default calls <see cref="WriteInitSegment(ReadOnlySpan{byte})"/> and
        /// returns <paramref name="fileName"/>.
        /// </summary>
        /// <param name="fileName">A bare file name chosen by the segmenter, e.g. <c>init.mp4</c>.</param>
        /// <param name="bytes">The init segment.</param>
        string WriteInitSegment(string fileName, ReadOnlySpan<byte> bytes)
        {
            WriteInitSegment(bytes);
            return fileName;
        }

        /// <summary>
        /// Writes one media segment under <paramref name="fileName"/> and returns the URI the
        /// playlist lists for it. <see cref="Hls.HlsSegmenter"/> calls this overload; the default
        /// calls <see cref="WriteMediaSegment(long, ReadOnlySpan{byte})"/> and returns
        /// <paramref name="fileName"/>.
        /// </summary>
        /// <param name="sequenceNumber">The segment's media sequence number.</param>
        /// <param name="fileName">A bare file name chosen by the segmenter, with the segment format's extension, e.g. <c>segment_7.m4s</c> or <c>segment_7.ts</c>.</param>
        /// <param name="bytes">The media segment.</param>
        string WriteMediaSegment(long sequenceNumber, string fileName, ReadOnlySpan<byte> bytes)
        {
            WriteMediaSegment(sequenceNumber, bytes);
            return fileName;
        }

        /// <summary>
        /// Called for a segment that has aged out of a live sliding window, with the URI
        /// <see cref="WriteMediaSegment(long, string, ReadOnlySpan{byte})"/> returned for it.
        /// <see cref="Hls.HlsSegmenter"/> calls this overload; the default calls
        /// <see cref="DeleteMediaSegment(long)"/>.
        /// </summary>
        void DeleteMediaSegment(long sequenceNumber, string uri) => DeleteMediaSegment(sequenceNumber);
    }
}
