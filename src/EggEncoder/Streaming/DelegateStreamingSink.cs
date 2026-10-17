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
        private readonly Action<byte[]> _onInitSegment;
        private readonly Action<long, byte[]> _onMediaSegment;
        private readonly Action<byte[]> _onPlaylist;
        private readonly Action<long> _onDeleteMediaSegment;

        public DelegateStreamingSink(
            Action<byte[]> onInitSegment,
            Action<long, byte[]> onMediaSegment,
            Action<byte[]> onPlaylist,
            Action<long> onDeleteMediaSegment)
        {
            ArgumentNullException.ThrowIfNull(onInitSegment);
            ArgumentNullException.ThrowIfNull(onMediaSegment);
            ArgumentNullException.ThrowIfNull(onPlaylist);
            ArgumentNullException.ThrowIfNull(onDeleteMediaSegment);

            _onInitSegment = onInitSegment;
            _onMediaSegment = onMediaSegment;
            _onPlaylist = onPlaylist;
            _onDeleteMediaSegment = onDeleteMediaSegment;
        }

        public void WriteInitSegment(ReadOnlySpan<byte> bytes) => _onInitSegment(bytes.ToArray());

        public void WriteMediaSegment(long sequenceNumber, ReadOnlySpan<byte> bytes) => _onMediaSegment(sequenceNumber, bytes.ToArray());

        public void WritePlaylist(ReadOnlySpan<byte> utf8Bytes) => _onPlaylist(utf8Bytes.ToArray());

        public void DeleteMediaSegment(long sequenceNumber) => _onDeleteMediaSegment(sequenceNumber);
    }
}
