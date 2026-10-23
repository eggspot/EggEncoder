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
        private readonly Dictionary<string, byte[]> _initSegments = [];

        /// <summary>The init segment written last.</summary>
        public byte[]? InitSegment { get; private set; }

        /// <summary>Every init segment written under a file name and not deleted, by that name (<c>init.mp4</c>, then <c>init_2.mp4</c> and so on after a configuration change).</summary>
        public IReadOnlyDictionary<string, byte[]> InitSegments => _initSegments;

        public byte[]? Playlist { get; private set; }

        public IReadOnlyDictionary<long, byte[]> MediaSegments => _mediaSegments;

        public void WriteInitSegment(ReadOnlySpan<byte> bytes) => InitSegment = bytes.ToArray();

        public void WriteMediaSegment(long sequenceNumber, ReadOnlySpan<byte> bytes) => _mediaSegments[sequenceNumber] = bytes.ToArray();

        public void WritePlaylist(ReadOnlySpan<byte> utf8Bytes) => Playlist = utf8Bytes.ToArray();

        public void DeleteMediaSegment(long sequenceNumber) => _mediaSegments.Remove(sequenceNumber);

        public string WriteInitSegment(string fileName, ReadOnlySpan<byte> bytes)
        {
            ArgumentException.ThrowIfNullOrEmpty(fileName);
            InitSegment = bytes.ToArray();
            _initSegments[fileName] = InitSegment;
            return fileName;
        }

        public void DeleteInitSegment(string uri)
        {
            ArgumentNullException.ThrowIfNull(uri);
            _initSegments.Remove(uri);
        }
    }
}
