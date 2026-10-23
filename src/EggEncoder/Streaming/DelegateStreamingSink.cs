namespace EggEncoder.Streaming
{
    /// <summary>
    /// Wraps caller-supplied delegates as an <see cref="IStreamingSink"/> -- the way to wire up a
    /// destination this library doesn't know about (an HTTP PUT, an S3 upload) without writing a
    /// full class. Every delegate receives a copied, durable <c>byte[]</c> (not the original
    /// <see cref="ReadOnlySpan{T}"/>, which cannot outlive the call), so it's free to hold onto or
    /// hand off the array however it needs to.
    /// </summary>
    public sealed class DelegateStreamingSink : IStreamingSink
    {
        private readonly Action<string, byte[]> _onInitSegment;
        private readonly Action<long, byte[]> _onMediaSegment;
        private readonly Action<byte[]> _onPlaylist;
        private readonly Action<long> _onDeleteMediaSegment;
        private readonly Action<string> _onDeleteInitSegment;

        /// <summary>
        /// For a stream with one init segment: <paramref name="onInitSegment"/> isn't told the init
        /// segment's file name, so use the other constructor when tracks can change mid-stream
        /// (<see cref="Hls.HlsSegmenter.UpdateTracks"/> writes <c>init_2.mp4</c> and so on).
        /// </summary>
        public DelegateStreamingSink(
            Action<byte[]> onInitSegment,
            Action<long, byte[]> onMediaSegment,
            Action<byte[]> onPlaylist,
            Action<long> onDeleteMediaSegment)
            : this(DropFileName(onInitSegment), onMediaSegment, onPlaylist, onDeleteMediaSegment, onDeleteInitSegment: _ => { })
        {
        }

        /// <summary>
        /// For a stream whose init segment can change: <paramref name="onInitSegment"/> receives
        /// each init segment's file name (<c>init.mp4</c>, <c>init_2.mp4</c>, ...), the URI the
        /// playlist lists for it, and <paramref name="onDeleteInitSegment"/> that URI once a live
        /// window no longer needs it.
        /// </summary>
        public DelegateStreamingSink(
            Action<string, byte[]> onInitSegment,
            Action<long, byte[]> onMediaSegment,
            Action<byte[]> onPlaylist,
            Action<long> onDeleteMediaSegment,
            Action<string> onDeleteInitSegment)
        {
            ArgumentNullException.ThrowIfNull(onInitSegment);
            ArgumentNullException.ThrowIfNull(onMediaSegment);
            ArgumentNullException.ThrowIfNull(onPlaylist);
            ArgumentNullException.ThrowIfNull(onDeleteMediaSegment);
            ArgumentNullException.ThrowIfNull(onDeleteInitSegment);

            _onInitSegment = onInitSegment;
            _onMediaSegment = onMediaSegment;
            _onPlaylist = onPlaylist;
            _onDeleteMediaSegment = onDeleteMediaSegment;
            _onDeleteInitSegment = onDeleteInitSegment;
        }

        public void WriteInitSegment(ReadOnlySpan<byte> bytes) => _onInitSegment(Hls.HlsSegmenter.InitSegmentUri, bytes.ToArray());

        public string WriteInitSegment(string fileName, ReadOnlySpan<byte> bytes)
        {
            ArgumentException.ThrowIfNullOrEmpty(fileName);
            _onInitSegment(fileName, bytes.ToArray());
            return fileName;
        }

        public void WriteMediaSegment(long sequenceNumber, ReadOnlySpan<byte> bytes) => _onMediaSegment(sequenceNumber, bytes.ToArray());

        public void WritePlaylist(ReadOnlySpan<byte> utf8Bytes) => _onPlaylist(utf8Bytes.ToArray());

        public void DeleteMediaSegment(long sequenceNumber) => _onDeleteMediaSegment(sequenceNumber);

        public void DeleteInitSegment(string uri)
        {
            ArgumentNullException.ThrowIfNull(uri);
            _onDeleteInitSegment(uri);
        }

        private static Action<string, byte[]> DropFileName(Action<byte[]> onInitSegment)
        {
            ArgumentNullException.ThrowIfNull(onInitSegment);
            return (_, bytes) => onInitSegment(bytes);
        }
    }
}
