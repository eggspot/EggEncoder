namespace EggEncoder.UnitTests.TestUtilities
{
    // A forward-read-only wrapper over a byte array, with CanSeek hard-false -- for testing that a
    // Stream-based codec entry point correctly rejects (or correctly tolerates) a non-seekable
    // source, the same shape a real network/HTTP response stream has. Wraps a MemoryStream
    // internally rather than reimplementing buffering, but deliberately hides Seek/Position/Length
    // behind NotSupportedException so a test can't accidentally pass by relying on seek support
    // this type is supposed to not have.
    public sealed class NonSeekableStream : Stream
    {
        private readonly MemoryStream _inner;

        public NonSeekableStream(byte[] data)
        {
            _inner = new MemoryStream(data);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

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
