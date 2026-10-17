namespace EggEncoder.UnitTests.TestUtilities
{
    // The write-side counterpart to NonSeekableStream: a forward-write-only wrapper over an
    // in-memory buffer, with CanSeek hard-false -- for testing that a Stream-based codec
    // destination entry point correctly rejects (or correctly tolerates) a non-seekable
    // destination, the same shape a real HTTP response body or other forward-only upload stream
    // has. Wraps a MemoryStream internally rather than reimplementing buffering, but deliberately
    // hides Seek/Position/Length behind NotSupportedException so a test can't accidentally pass by
    // relying on seek support this type is supposed to not have.
    public sealed class NonSeekableWriteStream : Stream
    {
        private readonly MemoryStream _inner = new();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override void Flush()
        {
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
