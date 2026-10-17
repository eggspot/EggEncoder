namespace EggEncoder.Streaming
{
    /// <summary>
    /// Writes an HLS output to a directory on disk: <c>{prefix}init.mp4</c>, <c>{prefix}segment_
    /// {n}.m4s</c> per media segment, and <c>{prefix}playlist.m3u8</c>, overwritten each time
    /// <see cref="WritePlaylist"/> is called. The directory must already exist.
    /// </summary>
    public sealed class FileSystemStreamingSink : IStreamingSink
    {
        private readonly string _directory;
        private readonly string _filePrefix;

        public FileSystemStreamingSink(string directory, string filePrefix = "")
        {
            ArgumentNullException.ThrowIfNull(directory);
            ArgumentNullException.ThrowIfNull(filePrefix);

            if (!Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException($"'{directory}' does not exist.");
            }

            _directory = directory;
            _filePrefix = filePrefix;
        }

        public void WriteInitSegment(ReadOnlySpan<byte> bytes) =>
            File.WriteAllBytes(Path.Combine(_directory, $"{_filePrefix}init.mp4"), bytes.ToArray());

        public void WriteMediaSegment(long sequenceNumber, ReadOnlySpan<byte> bytes) =>
            File.WriteAllBytes(Path.Combine(_directory, SegmentFileName(sequenceNumber)), bytes.ToArray());

        public void WritePlaylist(ReadOnlySpan<byte> utf8Bytes) =>
            File.WriteAllBytes(Path.Combine(_directory, $"{_filePrefix}playlist.m3u8"), utf8Bytes.ToArray());

        public void DeleteMediaSegment(long sequenceNumber)
        {
            var path = Path.Combine(_directory, SegmentFileName(sequenceNumber));
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private string SegmentFileName(long sequenceNumber) => $"{_filePrefix}segment_{sequenceNumber}.m4s";
    }
}
