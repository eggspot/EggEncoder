namespace EggEncoder.Streaming
{
    /// <summary>
    /// Keeps an HLS output entirely in memory -- no filesystem involved. The primary vehicle for
    /// this library's own tests, and generally useful for a caller that wants the produced bytes
    /// directly rather than files on disk.
    /// </summary>
    public sealed class InMemoryStreamingSink : IStreamingSink
    {
        private readonly Dictionary<long, byte[]> _mediaSegments = [];

        public byte[]? InitSegment { get; private set; }

        public byte[]? Playlist { get; private set; }

        public IReadOnlyDictionary<long, byte[]> MediaSegments => _mediaSegments;

        public void WriteInitSegment(ReadOnlySpan<byte> bytes) => InitSegment = bytes.ToArray();

        public void WriteMediaSegment(long sequenceNumber, ReadOnlySpan<byte> bytes) => _mediaSegments[sequenceNumber] = bytes.ToArray();

        public void WritePlaylist(ReadOnlySpan<byte> utf8Bytes) => Playlist = utf8Bytes.ToArray();

        public void DeleteMediaSegment(long sequenceNumber) => _mediaSegments.Remove(sequenceNumber);
    }
}
