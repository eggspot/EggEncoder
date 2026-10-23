namespace EggEncoder.Streaming
{
    /// <summary>
    /// Writes an HLS output to a directory on disk: <c>{prefix}playlist.m3u8</c>, overwritten each
    /// time <see cref="WritePlaylist"/> is called, next to the init and media segments under the
    /// names <see cref="Hls.HlsSegmenter"/> chooses, prefixed (<c>{prefix}init.mp4</c>,
    /// <c>{prefix}segment_{n}.m4s</c> or <c>{prefix}segment_{n}.ts</c>, and <c>{prefix}init_2.mp4</c> and
    /// so on after a configuration change). The URIs it returns for the
    /// playlist are those prefixed names, relative to the playlist, so several renditions can share
    /// one directory. The unnamed overloads write <c>{prefix}init.mp4</c> and
    /// <c>{prefix}segment_{n}.m4s</c>. The directory must already exist.
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

        public void DeleteMediaSegment(long sequenceNumber) => DeleteFile(SegmentFileName(sequenceNumber));

        public string WriteInitSegment(string fileName, ReadOnlySpan<byte> bytes) => WriteFile(fileName, bytes);

        public string WriteMediaSegment(long sequenceNumber, string fileName, ReadOnlySpan<byte> bytes) => WriteFile(fileName, bytes);

        public void DeleteMediaSegment(long sequenceNumber, string uri) => DeleteReturnedUri(uri);

        public void DeleteInitSegment(string uri) => DeleteReturnedUri(uri);

        private void DeleteReturnedUri(string uri)
        {
            ArgumentNullException.ThrowIfNull(uri);
            if (!uri.StartsWith(_filePrefix, StringComparison.Ordinal))
            {
                throw new ArgumentException($"'{uri}' is not a URI this sink returned.", nameof(uri));
            }

            RequireBareFileName(uri[_filePrefix.Length..], nameof(uri));
            DeleteFile(uri);
        }

        private static void RequireBareFileName(string fileName, string parameterName)
        {
            ArgumentException.ThrowIfNullOrEmpty(fileName, parameterName);
            if (fileName is "." or ".." || Path.GetFileName(fileName) != fileName)
            {
                throw new ArgumentException($"'{fileName}' must be a bare file name, with no directory part.", parameterName);
            }
        }

        private string WriteFile(string fileName, ReadOnlySpan<byte> bytes)
        {
            RequireBareFileName(fileName, nameof(fileName));
            var uri = _filePrefix + fileName;

            File.WriteAllBytes(Path.Combine(_directory, uri), bytes.ToArray());
            return uri;
        }

        private void DeleteFile(string relativePath)
        {
            var path = Path.Combine(_directory, relativePath);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private string SegmentFileName(long sequenceNumber) => $"{_filePrefix}segment_{sequenceNumber}.m4s";
    }
}
