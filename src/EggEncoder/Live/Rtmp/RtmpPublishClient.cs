using System.Buffers;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Security.Authentication;
using EggEncoder.Containers.Flv;
using EggEncoder.Live.Rtmp.Amf0;
using EggEncoder.Media;

namespace EggEncoder.Live.Rtmp
{
    /// <summary>
    /// Publishes a packet stream to an RTMP server (<c>rtmp://</c>, or <c>rtmps://</c> over TLS), as
    /// OBS or <c>ffmpeg -c copy -f flv rtmp://...</c> do: YouTube, Twitch, Facebook, any ingest server,
    /// or this library's own <see cref="RtmpServer"/>. <see cref="OpenAsync"/> connects, handshakes and
    /// sends <c>connect</c>, <c>releaseStream</c>, <c>FCPublish</c>, <c>createStream</c> and
    /// <c>publish</c> in ffmpeg's order, then <c>@setDataFrame</c>/<c>onMetaData</c>; each
    /// <see cref="WriteAsync"/> sends one packet as an FLV tag body (sequence headers first, as
    /// <see cref="FlvWriter"/> writes them); <see cref="CompleteAsync"/> sends <c>FCUnpublish</c> and
    /// <c>deleteStream</c> and closes. See docs/live-ingest-restream-plan.md item 6.
    /// </summary>
    /// <remarks>
    /// One publish per instance, with no reconnect: a failure closes the connection and throws
    /// <see cref="RtmpClientException"/>, and every later call throws it again (retrying belongs to the
    /// caller, such as a restream output's policy). The tracks are what <see cref="FlvWriter"/>
    /// accepts: at most one audio (AAC, MP3) and one video track (H.264, or H.265/AV1/VP9 as Enhanced
    /// RTMP, announced in <c>connect</c>'s <c>fourCcList</c>). Packets are sent as they come, with no
    /// real-time pacing. The stream key never appears in an exception message.
    /// </remarks>
    public sealed class RtmpPublishClient : IMediaPacketSink
    {
        /// <summary>The port of an <c>rtmp://</c> URL that names none.</summary>
        public const int DefaultRtmpPort = RtmpServerOptions.DefaultPort;

        /// <summary>The port of an <c>rtmps://</c> URL that names none.</summary>
        public const int DefaultRtmpsPort = 443;

        private const int CommandChunkStreamId = RtmpCommand.ChunkStreamId;
        private const int AudioChunkStreamId = 4;
        private const int DataChunkStreamId = 5;
        private const int VideoChunkStreamId = 6;
        private const int PublishChunkStreamId = 8;
        private const int ReadBufferLength = 64 * 1024;
        private const string TlsHandshakePhase = "the TLS handshake";

        private enum State
        {
            Created,
            Opening,
            Publishing,
            Completing,
            Completed,
            Failed
        }

        private readonly RtmpPublishClientOptions _options;
        private readonly string _host;
        private readonly int _port;
        private readonly bool _tls;
        private readonly string _app;
        private readonly string _streamKey;
        private readonly string _tcUrl;
        private readonly RtmpChunkWriter _writer = new();
        private readonly ArrayBufferWriter<byte> _output = new();
        private readonly Queue<RtmpMessage> _incoming = new();
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly CancellationTokenSource _closing = new();
        private readonly FlvTagMuxer _muxer;
        private readonly RtmpChunkReader _reader;
        private readonly byte[] _readBuffer = new byte[ReadBufferLength];

        private byte[] _messageScratch = new byte[64 * 1024];
        private volatile State _state;
        private Socket? _socket;
        private Stream? _stream;
        private uint _streamId;
        private double _nextTransactionId = 1;
        private Task _receiveLoop = Task.CompletedTask;
        private Exception? _failure;
        private string _phase = "connecting";
        private bool _disposed;

        /// <summary>
        /// A client for a server URL and a separate stream key, the way ingest services hand them out
        /// and OBS takes them (YouTube's <c>rtmp://a.rtmp.youtube.com/live2</c> plus the key it shows).
        /// The application is the server URL's path without its leading and trailing <c>/</c> (it may
        /// contain <c>/</c> itself); the port defaults to <see cref="DefaultRtmpPort"/> or
        /// <see cref="DefaultRtmpsPort"/>. The key is sent exactly as given, query string included
        /// (<c>key?bandwidthtest=true</c>).
        /// </summary>
        /// <exception cref="ArgumentException">Not an absolute <c>rtmp://</c> or <c>rtmps://</c> URL with a host and an application, a URL with user info, a query or a fragment (secrets belong in <paramref name="streamKey"/>), or an empty stream key.</exception>
        public RtmpPublishClient(Uri serverUrl, string streamKey, RtmpPublishClientOptions? options = null)
            : this(ParseServerUrl(serverUrl, streamKey), options)
        {
        }

        private RtmpPublishClient((string Host, int Port, bool Tls, string App, string StreamKey, string TcUrl) target, RtmpPublishClientOptions? options)
        {
            (_host, _port, _tls, _app, _streamKey, _tcUrl) = target;
            _options = options ?? new RtmpPublishClientOptions();
            _muxer = new FlvTagMuxer(WriteMediaMessage);
            _reader = new RtmpChunkReader(_incoming.Enqueue, _options.ChunkReader);
        }

        /// <summary>The application the client connects to (the <c>app</c> of <c>connect</c>).</summary>
        public string AppName => _app;

        /// <summary>The <c>tcUrl</c> sent in <c>connect</c>: the server URL without the stream key.</summary>
        public string TcUrl => _tcUrl;

        /// <summary>
        /// Connects, handshakes and starts the publish of these tracks, returning once the server has
        /// answered <see cref="RtmpStatusCodes.PublishStart"/> and the metadata is sent.
        /// </summary>
        /// <exception cref="NotSupportedException">A track set FLV can't carry (see <see cref="FlvWriter.Open"/>); nothing was sent.</exception>
        /// <exception cref="ArgumentException">A track with no codec configuration, or metadata that can't be encoded as AMF0; nothing was sent.</exception>
        /// <exception cref="RtmpClientException">The connection, the TLS or RTMP handshake, or the publish failed or timed out; the connection is closed.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; the connection is closed.</exception>
        public async ValueTask OpenAsync(MediaTrackSet tracks, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(tracks);
            if (_state != State.Created)
            {
                throw new InvalidOperationException("The client has already been opened");
            }

            _muxer.SetTracks(tracks);
            var metadata = RtmpDataMessage.SetDataFrame(_options.Metadata ?? FlvMetadata.FromTracks(tracks)).Encode();
            var fourCcList = tracks.Tracks
                .Where(t => t.Kind == MediaKind.Video && FlvTagMuxer.IsEnhancedVideoCodec(t.Codec))
                .Select(t => Amf0Value.FromString(FourCcString(t.Codec)))
                .ToArray();
            _state = State.Opening;

            using var timeout = new CancellationTokenSource(_options.ConnectTimeout, _options.TimeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token, _closing.Token);
            try
            {
                await ConnectAsync(linked.Token).ConfigureAwait(false);
                _phase = "the RTMP handshake";
                await HandshakeAsync(linked.Token).ConfigureAwait(false);

                timeout.CancelAfter(_options.PublishTimeout);
                await PublishAsync(fourCcList, linked.Token).ConfigureAwait(false);
                _writer.Write(_output, DataChunkStreamId, new RtmpMessage(RtmpMessageType.DataAmf0, _streamId, 0, metadata));
                await FlushAsync(linked.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                var failure = Classify(e, timedOut: timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested, connecting: _stream is null);
                await FailAsync(failure).ConfigureAwait(false);
                if (failure is null)
                {
                    throw;
                }

                throw failure;
            }

            _state = State.Publishing;
            _receiveLoop = Task.Run(ReceiveLoopAsync, CancellationToken.None);
        }

        /// <summary>Sends one packet, preceded by its track's sequence header when that hasn't been sent for this configuration yet.</summary>
        /// <exception cref="ArgumentException">An unknown track, or a <see cref="MediaPacket.ConfigVersion"/> that isn't the track's current one.</exception>
        /// <exception cref="ArgumentOutOfRangeException">A negative DTS, or a composition offset FLV can't carry.</exception>
        /// <exception cref="RtmpClientException">The connection failed, the server refused the stream, or the write timed out; the connection is closed.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled. A write cancelled part-way leaves the stream unusable, so the connection is closed.</exception>
        public async ValueTask WriteAsync(MediaPacket packet, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            ThrowIfFailed();
            ThrowIfNotPublishing();
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfFailed();
                cancellationToken.ThrowIfCancellationRequested();
                ExceptionDispatchInfo? invalidPacket = null;
                try
                {
                    _muxer.Write(packet);
                }
                catch (ArgumentException e)
                {
                    invalidPacket = ExceptionDispatchInfo.Capture(e);
                }

                await SendBufferedAsync(cancellationToken).ConfigureAwait(false);
                invalidPacket?.Throw();
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Ends the publish: sends <c>FCUnpublish</c> and <c>deleteStream</c>, then waits up to
        /// <see cref="RtmpPublishClientOptions.CloseTimeout"/> for the server to close the connection,
        /// and closes it.
        /// </summary>
        /// <exception cref="RtmpClientException">The connection failed before or while the unpublish was sent.</exception>
        public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            ThrowIfFailed();
            ThrowIfNotPublishing();
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfFailed();
                _state = State.Completing;
                WriteCommand(RtmpCommand.FCUnpublish(_nextTransactionId++, _streamKey));
                WriteCommand(RtmpCommand.DeleteStream(_nextTransactionId++, _streamId));
                await SendBufferedAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            using var closeTimeout = new CancellationTokenSource(_options.CloseTimeout, _options.TimeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closeTimeout.Token);
            try
            {
                await ShutdownSendAsync().ConfigureAwait(false);
                await _receiveLoop.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    await CloseTransportAsync().ConfigureAwait(false);
                    throw;
                }
            }

            _state = State.Completed;
            await CloseTransportAsync().ConfigureAwait(false);
        }

        /// <summary>Closes the connection, without an unpublish if <see cref="CompleteAsync"/> wasn't called. Calling it again does nothing.</summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await CloseTransportAsync().ConfigureAwait(false);
            await _receiveLoop.ConfigureAwait(false);
            _closing.Dispose();
        }

        private static (string Host, int Port, bool Tls, string App, string StreamKey, string TcUrl) ParseServerUrl(Uri serverUrl, string streamKey)
        {
            ArgumentNullException.ThrowIfNull(serverUrl);
            ArgumentNullException.ThrowIfNull(streamKey);
            if (!serverUrl.IsAbsoluteUri || serverUrl.Scheme is not ("rtmp" or "rtmps") || serverUrl.IdnHost.Length == 0)
            {
                throw new ArgumentException("Expected an absolute rtmp:// or rtmps:// URL with a host", nameof(serverUrl));
            }

            if (serverUrl.UserInfo.Length > 0 || serverUrl.Query.Length > 0 || serverUrl.Fragment.Length > 0)
            {
                throw new ArgumentException("An RTMP server URL must not carry user info, a query or a fragment; pass secrets in the stream key", nameof(serverUrl));
            }

            var app = Uri.UnescapeDataString(serverUrl.AbsolutePath).Trim('/');
            if (app.Length == 0)
            {
                throw new ArgumentException("An RTMP server URL needs an application: rtmp://host[:port]/app", nameof(serverUrl));
            }

            if (streamKey.Length == 0)
            {
                throw new ArgumentException("The stream key must not be empty", nameof(streamKey));
            }

            var tls = serverUrl.Scheme == "rtmps";
            var port = serverUrl.Port >= 0 ? serverUrl.Port : tls ? DefaultRtmpsPort : DefaultRtmpPort;
            var tcUrl = $"{serverUrl.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped)}/{app}";
            return (serverUrl.IdnHost, port, tls, app, streamKey, tcUrl);
        }

        private static string FourCcString(MediaCodec codec) => codec switch
        {
            MediaCodec.H265 => "hvc1",
            MediaCodec.Av1 => "av01",
            _ => "vp09"
        };

        private async Task ConnectAsync(CancellationToken cancellationToken)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            _socket = socket;
            await socket.ConnectAsync(_host, _port, cancellationToken).ConfigureAwait(false);
            Stream stream = new NetworkStream(socket, ownsSocket: true);
            _stream = stream;
            if (_tls)
            {
                _phase = TlsHandshakePhase;
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                _stream = ssl;
                await ssl.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions
                    {
                        TargetHost = _host,
                        RemoteCertificateValidationCallback = _options.RemoteCertificateValidation
                    },
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task HandshakeAsync(CancellationToken cancellationToken)
        {
            var handshake = RtmpHandshake.CreateClient();
            await _stream!.WriteAsync(handshake.Start(), cancellationToken).ConfigureAwait(false);
            while (!handshake.IsComplete)
            {
                var peer = new byte[handshake.BytesExpected];
                await _stream.ReadExactlyAsync(peer, cancellationToken).ConfigureAwait(false);
                var reply = handshake.Process(peer);
                if (reply.Length > 0)
                {
                    await _stream.WriteAsync(reply, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task PublishAsync(Amf0Value[] fourCcList, CancellationToken cancellationToken)
        {
            _phase = "connect";
            var connectId = _nextTransactionId++;
            var additional = fourCcList.Length == 0
                ? null
                : new[] { new KeyValuePair<string, Amf0Value>("fourCcList", Amf0Value.FromStrictArray(fourCcList)) };
            WriteCommand(RtmpCommand.Connect(connectId, _app, _tcUrl, additional));
            _writer.WriteSetChunkSize(_output, _options.OutboundChunkSize);
            await FlushAsync(cancellationToken).ConfigureAwait(false);
            await ReadUntilAsync(command => IsReplyTo(command, connectId), cancellationToken).ConfigureAwait(false);

            _phase = "createStream";
            WriteCommand(RtmpCommand.ReleaseStream(_nextTransactionId++, _streamKey));
            WriteCommand(RtmpCommand.FCPublish(_nextTransactionId++, _streamKey));
            var createStreamId = _nextTransactionId++;
            WriteCommand(RtmpCommand.CreateStream(createStreamId));
            await FlushAsync(cancellationToken).ConfigureAwait(false);
            RtmpCommand? created = null;
            await ReadUntilAsync(command => IsReplyTo(created = command, createStreamId), cancellationToken).ConfigureAwait(false);
            _streamId = ReadStreamId(created!);

            _phase = "publish";
            _writer.Write(_output, PublishChunkStreamId, RtmpCommand.Publish(_streamKey).ToMessage(_streamId));
            await FlushAsync(cancellationToken).ConfigureAwait(false);
            await ReadUntilAsync(IsPublishStart, cancellationToken).ConfigureAwait(false);
        }

        private bool IsReplyTo(RtmpCommand command, double transactionId)
        {
            if (command.TransactionId != transactionId)
            {
                return false;
            }

            return command.Name switch
            {
                "_result" => true,
                "_error" => throw Rejected(command),
                _ => false
            };
        }

        private bool IsPublishStart(RtmpCommand command)
        {
            if (command.Name != "onStatus")
            {
                return false;
            }

            if (IsErrorStatus(command))
            {
                throw Rejected(command);
            }

            return command.StatusCode == RtmpStatusCodes.PublishStart;
        }

        private static bool IsErrorStatus(RtmpCommand command) =>
            command.Arguments.Count > 0 && command.Arguments[0].GetProperty("level") is { Type: Amf0Type.String or Amf0Type.LongString } level
            && level.String == RtmpStatusCodes.LevelError;

        private RtmpClientException Rejected(RtmpCommand command) => new(
            RtmpClientFailureReason.Rejected,
            $"The RTMP server refused {_phase}{(command.StatusCode is { } code ? $" ({Truncate(code)})" : string.Empty)}",
            command.StatusCode is { } statusCode ? Truncate(statusCode) : null);

        private static uint ReadStreamId(RtmpCommand result)
        {
            var id = result.GetNumberArgument(0);
            if (id is not { } value || value < 1 || value > uint.MaxValue || value != Math.Floor(value))
            {
                throw new RtmpProtocolException("RTMP: the createStream result carries no valid stream id");
            }

            return (uint)value;
        }

        private async Task ReadUntilAsync(Func<RtmpCommand, bool> isDone, CancellationToken cancellationToken)
        {
            while (true)
            {
                while (_incoming.TryDequeue(out var message))
                {
                    if (HandleControl(message) || message.Type != RtmpMessageType.CommandAmf0)
                    {
                        continue;
                    }

                    if (isDone(RtmpCommand.Decode(message.Payload.Span)))
                    {
                        await FlushAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }

                await FlushAsync(cancellationToken).ConfigureAwait(false);
                var read = await _stream!.ReadAsync(_readBuffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("The server closed the connection");
                }

                Feed(read);
            }
        }

        private void Feed(int read)
        {
            _reader.Feed(_readBuffer.AsSpan(0, read));
            if (_reader.TryTakeAcknowledgement(out var sequenceNumber))
            {
                _writer.Write(_output, RtmpProtocolControl.ChunkStreamId, RtmpProtocolControl.CreateAcknowledgement(sequenceNumber));
            }
        }

        private bool HandleControl(RtmpMessage message)
        {
            switch (message.Type)
            {
                case RtmpMessageType.UserControl:
                    var control = RtmpUserControl.Read(message.Payload.Span);
                    if (control.EventType == RtmpUserControlEventType.PingRequest)
                    {
                        _writer.Write(_output, RtmpProtocolControl.ChunkStreamId, RtmpUserControl.PingResponse(control.Value).ToMessage());
                    }

                    return true;
                case RtmpMessageType.SetPeerBandwidth:
                    var (windowSize, _) = RtmpProtocolControl.ReadSetPeerBandwidth(message.Payload.Span);
                    _writer.Write(_output, RtmpProtocolControl.ChunkStreamId, RtmpProtocolControl.CreateWindowAcknowledgementSize(windowSize));
                    return true;
                case RtmpMessageType.CommandAmf3:
                    throw new RtmpProtocolException("RTMP: AMF3 commands are not supported");
                default:
                    return message.Type != RtmpMessageType.CommandAmf0;
            }
        }

        private async Task ReceiveLoopAsync()
        {
            try
            {
                while (true)
                {
                    var read = await _stream!.ReadAsync(_readBuffer, _closing.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        if (_state is State.Completing or State.Completed)
                        {
                            return;
                        }

                        throw new EndOfStreamException("The server closed the connection");
                    }

                    await _writeLock.WaitAsync(_closing.Token).ConfigureAwait(false);
                    try
                    {
                        Feed(read);
                        while (_incoming.TryDequeue(out var message))
                        {
                            if (!HandleControl(message) && message.Type == RtmpMessageType.CommandAmf0)
                            {
                                var command = RtmpCommand.Decode(message.Payload.Span);
                                if (command.Name == "onStatus" && IsErrorStatus(command))
                                {
                                    throw Rejected(command);
                                }
                            }
                        }

                        if (_state == State.Publishing)
                        {
                            await SendBufferedAsync(_closing.Token).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        _writeLock.Release();
                    }
                }
            }
            catch (Exception e) when (_closing.IsCancellationRequested && e is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
            }
            catch (Exception e)
            {
                if (_state is State.Completing or State.Completed && e is IOException or SocketException)
                {
                    return;
                }

                _phase = "the publish";
                await FailAsync(Classify(e, timedOut: false, connecting: false) ?? e).ConfigureAwait(false);
            }
        }

        private void WriteCommand(RtmpCommand command) => _writer.Write(_output, CommandChunkStreamId, command.ToMessage());

        private void WriteMediaMessage(FlvTagType type, uint timestamp, ReadOnlySpan<byte> bodyHeader, ReadOnlySpan<byte> bodyPayload)
        {
            var length = bodyHeader.Length + bodyPayload.Length;
            if (length > RtmpMessage.MaxPayloadLength)
            {
                throw new ArgumentException($"A {length}-byte message exceeds RTMP's {RtmpMessage.MaxPayloadLength}-byte limit", "packet");
            }

            if (_messageScratch.Length < length)
            {
                _messageScratch = new byte[Math.Max(length, _messageScratch.Length * 2)];
            }

            bodyHeader.CopyTo(_messageScratch);
            bodyPayload.CopyTo(_messageScratch.AsSpan(bodyHeader.Length));
            var messageType = type == FlvTagType.Audio ? RtmpMessageType.Audio : RtmpMessageType.Video;
            var chunkStreamId = type == FlvTagType.Audio ? AudioChunkStreamId : VideoChunkStreamId;
            _writer.Write(_output, chunkStreamId, new RtmpMessage(messageType, _streamId, timestamp, _messageScratch.AsMemory(0, length)));
        }

        private async ValueTask FlushAsync(CancellationToken cancellationToken)
        {
            if (_output.WrittenCount == 0)
            {
                return;
            }

            await _stream!.WriteAsync(_output.WrittenMemory, cancellationToken).ConfigureAwait(false);
            _output.ResetWrittenCount();
        }

        private async ValueTask SendBufferedAsync(CancellationToken cancellationToken)
        {
            using var timeout = new CancellationTokenSource(_options.WriteTimeout, _options.TimeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token, _closing.Token);
            try
            {
                await FlushAsync(linked.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _output.ResetWrittenCount();
                var failure = Classify(e, timedOut: timeout.IsCancellationRequested, connecting: false)
                    ?? new RtmpClientException(RtmpClientFailureReason.ConnectionLost, "An RTMP write was cancelled part-way, so the stream can't continue", innerException: e);
                await FailAsync(failure).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                ThrowIfFailed();
            }
        }

        private RtmpClientException? Classify(Exception e, bool timedOut, bool connecting) => e switch
        {
            RtmpClientException client => client,
            _ when timedOut => new RtmpClientException(RtmpClientFailureReason.Timeout, $"RTMP: {_phase} timed out", innerException: e),
            RtmpProtocolException => new RtmpClientException(RtmpClientFailureReason.ProtocolError, $"RTMP: the server broke the protocol during {_phase}", innerException: e),
            AuthenticationException => TlsFailed(e),
            IOException when _phase == TlsHandshakePhase => TlsFailed(e),
            SocketException when connecting => new RtmpClientException(RtmpClientFailureReason.ConnectFailed, $"RTMP: could not connect to {_host}:{_port}", innerException: e),
            IOException or SocketException or ObjectDisposedException =>
                new RtmpClientException(RtmpClientFailureReason.ConnectionLost, $"RTMP: the connection was lost during {_phase}", innerException: e),
            _ => null
        };

        private static RtmpClientException TlsFailed(Exception e) =>
            new(RtmpClientFailureReason.TlsFailed, "RTMP: the TLS handshake with the server failed", innerException: e);

        private async ValueTask FailAsync(Exception? failure)
        {
            if (failure is not null)
            {
                Interlocked.CompareExchange(ref _failure, failure, null);
            }

            _state = State.Failed;
            await CloseTransportAsync().ConfigureAwait(false);
        }

        private async ValueTask ShutdownSendAsync()
        {
            if (_stream is SslStream ssl)
            {
                await ssl.ShutdownAsync().ConfigureAwait(false);
            }

            _socket!.Shutdown(SocketShutdown.Send);
        }

        private async ValueTask CloseTransportAsync()
        {
            try
            {
                _closing.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            if (Interlocked.Exchange(ref _stream, null) is { } stream)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            _socket?.Dispose();
        }

        private void ThrowIfFailed()
        {
            if (Volatile.Read(ref _failure) is { } failure)
            {
                ExceptionDispatchInfo.Throw(failure);
            }
        }

        private void ThrowIfNotPublishing()
        {
            if (_state != State.Publishing)
            {
                throw new InvalidOperationException(_state switch
                {
                    State.Created or State.Opening => "OpenAsync must complete first",
                    State.Failed => "The client failed or was cancelled; publish again with a new client",
                    _ => "The publish has already completed"
                });
            }
        }

        private static string Truncate(string value) => value.Length <= 128 ? value : value[..128];

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
