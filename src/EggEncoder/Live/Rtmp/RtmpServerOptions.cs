using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;

namespace EggEncoder.Live.Rtmp
{
    /// <summary>
    /// Configuration for <see cref="RtmpServer"/>, with the defaults docs/live-ingest-restream-plan.md
    /// "Security posture" sets: listen on <c>127.0.0.1:1935</c> only, at most 64 connections, every
    /// phase of a connection timed out, and no publish accepted unless
    /// <see cref="AuthorizePublishAsync"/> says yes. Every bound has a hard upper limit no configuration
    /// can exceed; a value outside its range throws <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    public sealed class RtmpServerOptions
    {
        /// <summary>The standard RTMP port.</summary>
        public const int DefaultPort = 1935;

        /// <summary>The hard upper limit of <see cref="MaxConnections"/>.</summary>
        public const int HardMaxConnections = 10_000;

        /// <summary>The hard upper limit of <see cref="MaxNameLength"/>.</summary>
        public const int HardMaxNameLength = 4096;

        /// <summary>The hard upper limit of <see cref="PacketQueueCapacity"/>.</summary>
        public const int HardMaxPacketQueueCapacity = 1024 * 1024;

        /// <summary>The hard upper limit of <see cref="PacketQueueMaxBytes"/>.</summary>
        public const int HardMaxPacketQueueBytes = 1024 * 1024 * 1024;

        /// <summary>The hard upper limit of every timeout.</summary>
        public static readonly TimeSpan HardMaxTimeout = TimeSpan.FromHours(1);

        private IPEndPoint _endPoint = new(IPAddress.Loopback, DefaultPort);
        private RtmpChunkReaderOptions _chunkReader = new();
        private TimeProvider _timeProvider = TimeProvider.System;
        private int _maxConnections = 64;
        private int _maxNameLength = 256;
        private int _outboundChunkSize = 4096;
        private uint _acknowledgementWindowSize = 2_500_000;
        private int _packetQueueCapacity = 1024;
        private int _packetQueueMaxBytes = 64 * 1024 * 1024;
        private TimeSpan _handshakeTimeout = TimeSpan.FromSeconds(10);
        private TimeSpan _publishTimeout = TimeSpan.FromSeconds(10);
        private TimeSpan _idleTimeout = TimeSpan.FromSeconds(30);
        private TimeSpan _consumerStallTimeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// The address and port to listen on (default <c>127.0.0.1:1935</c>). Listening on every
        /// interface (<see cref="IPAddress.Any"/>/<see cref="IPAddress.IPv6Any"/>) is an explicit
        /// choice; port 0 picks a free port, which <see cref="RtmpServer.LocalEndPoint"/> reports.
        /// </summary>
        public IPEndPoint EndPoint
        {
            get => _endPoint;
            init => _endPoint = value ?? throw new ArgumentNullException(nameof(EndPoint));
        }

        /// <summary>
        /// When set, every connection is RTMPS: TLS (via <see cref="System.Net.Security.SslStream"/>)
        /// with this certificate, which must carry its private key, before the RTMP handshake.
        /// <c>null</c> (the default) is plain RTMP.
        /// </summary>
        public X509Certificate2? Certificate { get; init; }

        /// <summary>
        /// Decides whether a <c>publish</c> may go ahead, before any media from it is accepted.
        /// <c>null</c> (the default) rejects every publish with
        /// <see cref="RtmpStatusCodes.PublishBadName"/>: a server that accepts every stream key has to
        /// say so (<c>(_, _) =&gt; ValueTask.FromResult(true)</c>). The token is cancelled when the
        /// connection's <see cref="PublishTimeout"/> runs out or the server stops; an exception from the
        /// callback rejects the publish and closes that connection.
        /// </summary>
        public Func<RtmpPublishRequest, CancellationToken, ValueTask<bool>>? AuthorizePublishAsync { get; init; }

        /// <summary>The bounds on what a peer's chunk stream may ask for (chunk size, message length, chunk streams, buffered bytes).</summary>
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

        /// <summary>Connections open at once (default 64); one more is closed as soon as it is accepted.</summary>
        public int MaxConnections
        {
            get => _maxConnections;
            init => _maxConnections = Validate(value, 1, HardMaxConnections);
        }

        /// <summary>
        /// The longest application name or stream key accepted, in characters (default 256). Longer
        /// names, empty names and names with control characters are refused before any callback sees them.
        /// </summary>
        public int MaxNameLength
        {
            get => _maxNameLength;
            init => _maxNameLength = Validate(value, 1, HardMaxNameLength);
        }

        /// <summary>The chunk size the server sends with (default 4,096, what OBS and ffmpeg use).</summary>
        public int OutboundChunkSize
        {
            get => _outboundChunkSize;
            init => _outboundChunkSize = Validate(value, RtmpChunkReader.DefaultChunkSize, RtmpChunkReaderOptions.HardMaxInboundChunkSize);
        }

        /// <summary>The Window Acknowledgement Size the server asks publishers for, in bytes (default 2,500,000).</summary>
        public uint AcknowledgementWindowSize
        {
            get => _acknowledgementWindowSize;
            init => _acknowledgementWindowSize = value == 0
                ? throw new ArgumentOutOfRangeException(nameof(AcknowledgementWindowSize), value, "Must be positive.")
                : value;
        }

        /// <summary>
        /// Packets one publish may have waiting for its consumer (default 1,024). When the queue is full
        /// the server stops reading from the publisher (TCP backpressure) until the consumer catches up,
        /// for at most <see cref="ConsumerStallTimeout"/>.
        /// </summary>
        public int PacketQueueCapacity
        {
            get => _packetQueueCapacity;
            init => _packetQueueCapacity = Validate(value, 1, HardMaxPacketQueueCapacity);
        }

        /// <summary>Payload bytes one publish may have waiting for its consumer (default 64 MiB); treated like a full <see cref="PacketQueueCapacity"/>.</summary>
        public int PacketQueueMaxBytes
        {
            get => _packetQueueMaxBytes;
            init => _packetQueueMaxBytes = Validate(value, 1, HardMaxPacketQueueBytes);
        }

        /// <summary>How long the TLS and RTMP handshakes may take together (default 10 s).</summary>
        public TimeSpan HandshakeTimeout
        {
            get => _handshakeTimeout;
            init => _handshakeTimeout = ValidateTimeout(value);
        }

        /// <summary>How long from the end of the handshake until a <c>publish</c> is accepted, authorization included (default 10 s).</summary>
        public TimeSpan PublishTimeout
        {
            get => _publishTimeout;
            init => _publishTimeout = ValidateTimeout(value);
        }

        /// <summary>How long a publishing connection may send nothing before it is closed (default 30 s).</summary>
        public TimeSpan IdleTimeout
        {
            get => _idleTimeout;
            init => _idleTimeout = ValidateTimeout(value);
        }

        /// <summary>
        /// How long a publish may wait for its consumer to make room in a full packet queue before the
        /// publisher is disconnected (default 10 s). The idle timeout is paused meanwhile, since the stall
        /// is the server's own.
        /// </summary>
        public TimeSpan ConsumerStallTimeout
        {
            get => _consumerStallTimeout;
            init => _consumerStallTimeout = ValidateTimeout(value);
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
            if (value <= TimeSpan.Zero || value > HardMaxTimeout)
            {
                throw new ArgumentOutOfRangeException(name, value, $"Must be positive and at most {HardMaxTimeout}.");
            }

            return value;
        }
    }
}
