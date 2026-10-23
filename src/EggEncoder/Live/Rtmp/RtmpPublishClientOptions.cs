using System.Net.Security;
using System.Runtime.CompilerServices;
using EggEncoder.Containers.Flv;

namespace EggEncoder.Live.Rtmp
{
    /// <summary>
    /// Configuration for <see cref="RtmpPublishClient"/>. Every step has a timeout (docs/live-ingest-restream-plan.md
    /// "Security posture"), every bound on what the server may send has a hard upper limit, and a value
    /// outside its range throws <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    public sealed class RtmpPublishClientOptions
    {
        private RtmpChunkReaderOptions _chunkReader = new();
        private TimeProvider _timeProvider = TimeProvider.System;
        private int _outboundChunkSize = 4096;
        private TimeSpan _connectTimeout = TimeSpan.FromSeconds(10);
        private TimeSpan _publishTimeout = TimeSpan.FromSeconds(10);
        private TimeSpan _writeTimeout = TimeSpan.FromSeconds(30);
        private TimeSpan _closeTimeout = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Checks the server's certificate on an <c>rtmps://</c> connection instead of the default
        /// validation (the system's trusted roots and the URL's host name). <c>null</c>, the default,
        /// keeps the default validation; set it only to trust a specific certificate, never to accept
        /// every certificate outside a test.
        /// </summary>
        public RemoteCertificateValidationCallback? RemoteCertificateValidation { get; init; }

        /// <summary>The <c>onMetaData</c> sent after the publish starts; <c>null</c>, the default, sends <see cref="FlvMetadata.FromTracks"/> of the opened tracks.</summary>
        public FlvMetadata? Metadata { get; init; }

        /// <summary>The bounds on what the server's chunk stream may ask for (chunk size, message length, chunk streams, buffered bytes).</summary>
        public RtmpChunkReaderOptions ChunkReader
        {
            get => _chunkReader;
            init => _chunkReader = value ?? throw new ArgumentNullException(nameof(ChunkReader));
        }

        /// <summary>The clock every timeout runs on (default <see cref="TimeProvider.System"/>); tests pass a fake one.</summary>
        public TimeProvider TimeProvider
        {
            get => _timeProvider;
            init => _timeProvider = value ?? throw new ArgumentNullException(nameof(TimeProvider));
        }

        /// <summary>The chunk size the client sends with (default 4,096, what OBS and ffmpeg use).</summary>
        public int OutboundChunkSize
        {
            get => _outboundChunkSize;
            init => _outboundChunkSize = Validate(value, RtmpChunkReader.DefaultChunkSize, RtmpChunkReaderOptions.HardMaxInboundChunkSize);
        }

        /// <summary>How long the TCP connect, the TLS handshake and the RTMP handshake may take together (default 10 s).</summary>
        public TimeSpan ConnectTimeout
        {
            get => _connectTimeout;
            init => _connectTimeout = ValidateTimeout(value);
        }

        /// <summary>How long from the end of the handshake until the server accepts the publish (default 10 s).</summary>
        public TimeSpan PublishTimeout
        {
            get => _publishTimeout;
            init => _publishTimeout = ValidateTimeout(value);
        }

        /// <summary>How long one <see cref="RtmpPublishClient.WriteAsync"/> or <see cref="RtmpPublishClient.CompleteAsync"/> may wait for the server to take its bytes (default 30 s).</summary>
        public TimeSpan WriteTimeout
        {
            get => _writeTimeout;
            init => _writeTimeout = ValidateTimeout(value);
        }

        /// <summary>How long <see cref="RtmpPublishClient.CompleteAsync"/> waits for the server to close the connection after the unpublish before closing it itself (default 2 s).</summary>
        public TimeSpan CloseTimeout
        {
            get => _closeTimeout;
            init => _closeTimeout = ValidateTimeout(value);
        }

        private static int Validate(int value, int min, int max, [CallerMemberName] string? name = null)
        {
            if (value < min || value > max)
            {
                throw new ArgumentOutOfRangeException(name, value, $"Must be between {min} and {max}.");
            }

            return value;
        }

        private static TimeSpan ValidateTimeout(TimeSpan value, [CallerMemberName] string? name = null)
        {
            if (value <= TimeSpan.Zero || value > RtmpServerOptions.HardMaxTimeout)
            {
                throw new ArgumentOutOfRangeException(name, value, $"Must be positive and at most {RtmpServerOptions.HardMaxTimeout}.");
            }

            return value;
        }
    }
}
