namespace EggEncoder.Live.Rtmp
{
    /// <summary>
    /// The bounds <see cref="RtmpChunkReader"/> enforces on what a peer sends (docs/live-ingest-restream-plan.md
    /// "Security posture"). Every value has a default suited to real publishers (OBS and ffmpeg use a
    /// 4,096-byte chunk size and a handful of chunk streams) and a hard upper limit no configuration can
    /// exceed; a value outside 1..its hard limit throws <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    public sealed class RtmpChunkReaderOptions
    {
        /// <summary>The hard upper limit of <see cref="MaxInboundChunkSize"/>: a chunk larger than the largest message is pointless.</summary>
        public const int HardMaxInboundChunkSize = RtmpMessage.MaxPayloadLength;

        /// <summary>The hard upper limit of <see cref="MaxMessageLength"/>: what the 24-bit length field can describe.</summary>
        public const int HardMaxMessageLength = RtmpMessage.MaxPayloadLength;

        /// <summary>The hard upper limit of <see cref="MaxChunkStreams"/>.</summary>
        public const int HardMaxChunkStreams = 1024;

        /// <summary>The hard upper limit of <see cref="MaxBufferedBytes"/>.</summary>
        public const int HardMaxBufferedBytes = 256 * 1024 * 1024;

        private int _maxInboundChunkSize = 64 * 1024;
        private int _maxMessageLength = 8 * 1024 * 1024;
        private int _maxChunkStreams = 64;
        private int _maxBufferedBytes = 16 * 1024 * 1024;

        /// <summary>
        /// The largest chunk size a peer's Set Chunk Size may ask for (default 65,536). The specification
        /// allows up to 2^31-1, which would let one chunk header demand gigabytes.
        /// </summary>
        public int MaxInboundChunkSize
        {
            get => _maxInboundChunkSize;
            init => _maxInboundChunkSize = Validate(value, HardMaxInboundChunkSize);
        }

        /// <summary>The largest message a peer may send (default 8 MiB, enough for a large 4K keyframe).</summary>
        public int MaxMessageLength
        {
            get => _maxMessageLength;
            init => _maxMessageLength = Validate(value, HardMaxMessageLength);
        }

        /// <summary>How many distinct chunk stream ids a peer may use on one connection (default 64).</summary>
        public int MaxChunkStreams
        {
            get => _maxChunkStreams;
            init => _maxChunkStreams = Validate(value, HardMaxChunkStreams);
        }

        /// <summary>
        /// The most bytes of partially received messages held at once, across every chunk stream of a
        /// connection (default 16 MiB). Buffers grow only as payload actually arrives, so a header that
        /// merely claims a large length costs nothing until the bytes come.
        /// </summary>
        public int MaxBufferedBytes
        {
            get => _maxBufferedBytes;
            init => _maxBufferedBytes = Validate(value, HardMaxBufferedBytes);
        }

        private static int Validate(int value, int hardLimit, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            if (value < 1 || value > hardLimit)
            {
                throw new ArgumentOutOfRangeException(name, value, $"Must be between 1 and {hardLimit}.");
            }

            return value;
        }
    }
}
