using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using EggEncoder.Containers.Flv;
using EggEncoder.Live.Rtmp.Amf0;
using EggEncoder.Media;

namespace EggEncoder.Live.Rtmp
{
    /// <summary>
    /// One accepted connection of an <see cref="RtmpServer"/>: TLS (for RTMPS), the handshake, the
    /// NetConnection/NetStream command flow up to one <c>publish</c>, then the publisher's media into
    /// an <see cref="RtmpIngestStream"/>. Everything runs on one task; nothing here throws out of
    /// <see cref="RunAsync"/>.
    /// </summary>
    internal sealed class RtmpServerConnection
    {
        private const int ReadBufferLength = 64 * 1024;
        private const int CommandChunkStreamId = RtmpCommand.ChunkStreamId;
        private const int StatusChunkStreamId = 5;

        private readonly RtmpServer _server;
        private readonly RtmpServerOptions _options;
        private readonly Socket _socket;
        private readonly EndPoint? _remoteEndPoint;
        private readonly CancellationTokenSource _timeout;
        private readonly CancellationTokenSource _stall;
        private readonly RtmpChunkWriter _writer = new();
        private readonly ArrayBufferWriter<byte> _output = new();
        private readonly List<RtmpMessage> _incoming = [];
        // Demuxed entries not yet handed to the ingest stream; an entry leaves only once it is queued,
        // so a stall that interrupts a batch never queues anything twice.
        private readonly Queue<(MediaTrackSet? Tracks, MediaPacket Packet)> _produced = new();
        private readonly FlvTagDemuxer _tags;

        private IReadOnlyList<KeyValuePair<string, Amf0Value>>? _connectProperties;
        private string? _appName;
        private uint _lastCreatedStreamId;
        private bool _publishAttempted;
        private bool _closeRequested;
        private bool _tracksDecided;

        private RtmpIngestStream? _ingest;
        private uint _publishStreamId;
        private bool _publishEnded;
        private volatile bool _finished;

        public RtmpServerConnection(RtmpServer server, Socket socket)
        {
            _server = server;
            _options = server.Options;
            _socket = socket;
            _remoteEndPoint = TryGetRemoteEndPoint(socket);
            _timeout = new CancellationTokenSource(Timeout.InfiniteTimeSpan, _options.TimeProvider);
            _stall = new CancellationTokenSource(Timeout.InfiniteTimeSpan, _options.TimeProvider);
            _tags = new FlvTagDemuxer(tracks => _produced.Enqueue((tracks, default)), packet => _produced.Enqueue((null, packet)));
        }

        /// <summary>Ends the connection from outside (<see cref="RtmpIngestStream.Disconnect"/>, or the server is stopping).</summary>
        public void Disconnect()
        {
            if (_finished)
            {
                return;
            }

            Cancel(_stall);
            Cancel(_timeout);
        }

        // Each on its own, so a throwing registration on one can't leave the other running.
        private static void Cancel(CancellationTokenSource source)
        {
            try
            {
                source.Cancel();
            }
            catch (Exception e) when (e is ObjectDisposedException or AggregateException)
            {
                // Finishing at the same moment, or a callback registered on the token (by an
                // authorization callback) threw; either way the connection is going down.
            }
        }

        public async Task RunAsync(CancellationToken serverToken)
        {
            Exception? error = null;
            Stream? stream = null;
            using var stopping = serverToken.Register(Disconnect);
            try
            {
                _timeout.CancelAfter(_options.HandshakeTimeout);
                stream = new NetworkStream(_socket, ownsSocket: true);
                if (_options.Certificate is { } certificate)
                {
                    var tls = new SslStream(stream, leaveInnerStreamOpen: false);
                    stream = tls;
                    await tls.AuthenticateAsServerAsync(
                        new SslServerAuthenticationOptions { ServerCertificate = certificate, ClientCertificateRequired = false },
                        _timeout.Token).ConfigureAwait(false);
                }

                await HandshakeAsync(stream).ConfigureAwait(false);
                _timeout.CancelAfter(_options.PublishTimeout);
                await MessageLoopAsync(stream).ConfigureAwait(false);
            }
            catch (RtmpProtocolException e)
            {
                error = e;
            }
            catch (InvalidDataException e)
            {
                // A malformed FLV tag body (sequence header) inside an otherwise valid message.
                error = new RtmpProtocolException($"RTMP: malformed media from the publisher ({e.Message})", e);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or SocketException
                or AuthenticationException or ObjectDisposedException)
            {
                // A timeout, a disconnect, a TLS failure or the server stopping: the publish (if any)
                // ends normally.
            }
            catch (Exception e)
            {
                // A PublishStarted handler or the authorization callback threw: close this
                // connection only, and end its publish as faulted so a consumer knows.
                error = e;
            }
            finally
            {
                EndPublish(error);
                try
                {
                    if (stream is not null)
                    {
                        await stream.DisposeAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
                {
                    // Closing a connection that is already broken.
                }
                finally
                {
                    _socket.Dispose();
                }

                _finished = true;
                _timeout.Dispose();
                _stall.Dispose();
            }
        }

        private async Task HandshakeAsync(Stream stream)
        {
            var handshake = RtmpHandshake.CreateServer();
            handshake.Start();
            while (!handshake.IsComplete)
            {
                var peerBytes = new byte[handshake.BytesExpected];
                await stream.ReadExactlyAsync(peerBytes, _timeout.Token).ConfigureAwait(false);
                var reply = handshake.Process(peerBytes);
                if (reply.Length > 0)
                {
                    await stream.WriteAsync(reply, _timeout.Token).ConfigureAwait(false);
                }
            }
        }

        private async Task MessageLoopAsync(Stream stream)
        {
            var reader = new RtmpChunkReader(_incoming.Add, _options.ChunkReader);
            var buffer = new byte[ReadBufferLength];
            while (!_closeRequested)
            {
                // Until publish, the publish deadline runs; after it, the idle deadline, which only
                // media pushes back (pings and acknowledgements don't keep a publish alive).
                var read = await stream.ReadAsync(buffer, _timeout.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    // The peer closed. A message it left unfinished is dropped: a publisher that
                    // disconnects mid-message ends its publish normally.
                    return;
                }

                reader.Feed(buffer.AsSpan(0, read));
                foreach (var message in _incoming)
                {
                    await HandleMessageAsync(stream, message).ConfigureAwait(false);
                    if (_closeRequested)
                    {
                        break;
                    }
                }

                _incoming.Clear();
                if (reader.TryTakeAcknowledgement(out var sequenceNumber))
                {
                    _writer.Write(_output, RtmpProtocolControl.ChunkStreamId, RtmpProtocolControl.CreateAcknowledgement(sequenceNumber));
                }

                await FlushAsync(stream).ConfigureAwait(false);
            }
        }

        private async ValueTask HandleMessageAsync(Stream stream, RtmpMessage message)
        {
            switch (message.Type)
            {
                case RtmpMessageType.CommandAmf0:
                    await HandleCommandAsync(stream, message).ConfigureAwait(false);
                    break;
                case RtmpMessageType.Audio:
                case RtmpMessageType.Video:
                case RtmpMessageType.DataAmf0:
                    if (IsPublishing(message))
                    {
                        await HandleMediaAsync(message).ConfigureAwait(false);
                    }

                    break;
                case RtmpMessageType.UserControl:
                    var control = RtmpUserControl.Read(message.Payload.Span);
                    if (control.EventType == RtmpUserControlEventType.PingRequest)
                    {
                        _writer.Write(_output, RtmpProtocolControl.ChunkStreamId, RtmpUserControl.PingResponse(control.Value).ToMessage());
                    }

                    break;
                case RtmpMessageType.CommandAmf3:
                    throw new RtmpProtocolException("RTMP: AMF3 commands are not supported");
            }

            // Set Chunk Size, Abort and Window Acknowledgement Size were applied by the chunk reader;
            // acknowledgements, bandwidth, AMF3 data and shared objects need nothing from an ingest server.
        }

        private bool IsPublishing(RtmpMessage message) => _ingest is not null && !_publishEnded && message.StreamId == _publishStreamId;

        private async ValueTask HandleCommandAsync(Stream stream, RtmpMessage message)
        {
            var command = RtmpCommand.Decode(message.Payload.Span);
            if (_connectProperties is null && command.Name != "connect")
            {
                throw new RtmpProtocolException($"RTMP: '{Truncate(command.Name)}' before connect");
            }

            switch (command.Name)
            {
                case "connect":
                    HandleConnect(command);
                    break;
                case "createStream":
                    _lastCreatedStreamId++;
                    WriteCommand(RtmpCommand.CreateStreamResult(command.TransactionId, _lastCreatedStreamId));
                    break;
                case "publish":
                    await HandlePublishAsync(stream, message.StreamId, command).ConfigureAwait(false);
                    break;
                case "FCUnpublish":
                    ReplyIfExpected(command);
                    CloseAfterPublish();
                    break;
                case "deleteStream":
                    if (_ingest is not null && command.GetNumberArgument(0) == _publishStreamId)
                    {
                        CloseAfterPublish();
                    }

                    break;
                case "play":
                    WriteStatus(message.StreamId, RtmpStatusCodes.LevelError, RtmpStatusCodes.PlayFailed, "This server accepts publishing only.");
                    _closeRequested = true;
                    break;
                case "releaseStream":
                case "FCPublish":
                case "getStreamLength":
                    ReplyIfExpected(command);
                    break;
                default:
                    if (command.TransactionId > 0)
                    {
                        WriteCommand(RtmpCommand.Error(command.TransactionId, RtmpStatusCodes.CallFailed, "Unsupported call."));
                    }

                    break;
            }
        }

        private void HandleConnect(RtmpCommand command)
        {
            if (_connectProperties is not null)
            {
                throw new RtmpProtocolException("RTMP: a second connect on one connection");
            }

            var app = command.CommandObject.GetProperty("app") is { Type: Amf0Type.String or Amf0Type.LongString } value ? value.String! : null;
            if (!IsValidName(app))
            {
                WriteCommand(RtmpCommand.Error(command.TransactionId, RtmpStatusCodes.ConnectRejected, "Invalid application name."));
                _closeRequested = true;
                return;
            }

            _appName = app;
            _connectProperties = command.CommandObject.Properties;
            _writer.Write(_output, RtmpProtocolControl.ChunkStreamId, RtmpProtocolControl.CreateWindowAcknowledgementSize(_options.AcknowledgementWindowSize));
            _writer.Write(_output, RtmpProtocolControl.ChunkStreamId, RtmpProtocolControl.CreateSetPeerBandwidth(_options.AcknowledgementWindowSize, RtmpPeerBandwidthLimitType.Dynamic));
            _writer.WriteSetChunkSize(_output, _options.OutboundChunkSize);
            WriteCommand(RtmpCommand.ConnectResult(command.TransactionId));
        }

        private async ValueTask HandlePublishAsync(Stream stream, uint streamId, RtmpCommand command)
        {
            var key = command.GetStringArgument(0);
            if (_publishAttempted || streamId == 0 || streamId > _lastCreatedStreamId || !IsValidName(key))
            {
                RejectPublish(streamId, "Invalid or repeated publish.");
                return;
            }

            _publishAttempted = true;
            var app = _appName!;
            if (!_server.TryReservePublish(app, key!))
            {
                RejectPublish(streamId, "The stream is already being published.");
                return;
            }

            var request = new RtmpPublishRequest(app, key!, command.GetStringArgument(1) ?? string.Empty, _remoteEndPoint, _connectProperties!);
            bool authorized;
            try
            {
                // The publish timeout keeps running through the callback, and holds even for a
                // callback that ignores its token.
                authorized = _options.AuthorizePublishAsync is { } authorize
                    && await authorize(request, _timeout.Token).AsTask().WaitAsync(_timeout.Token).ConfigureAwait(false);
            }
            catch
            {
                _server.ReleasePublish(app, key!);
                throw;
            }

            if (!authorized)
            {
                _server.ReleasePublish(app, key!);
                RejectPublish(streamId, "Not authorized.");
                return;
            }

            _publishStreamId = streamId;
            _timeout.CancelAfter(_options.IdleTimeout);
            _ingest = new RtmpIngestStream(request, _options.PacketQueueCapacity, _options.PacketQueueMaxBytes, Disconnect);
            _server.RaisePublishStarted(_ingest);
            _writer.Write(_output, RtmpProtocolControl.ChunkStreamId, RtmpUserControl.StreamBegin(streamId).ToMessage());
            WriteStatus(streamId, RtmpStatusCodes.LevelStatus, RtmpStatusCodes.PublishStart, "Publishing.");
            await FlushAsync(stream).ConfigureAwait(false);
        }

        private void RejectPublish(uint streamId, string description)
        {
            WriteStatus(streamId, RtmpStatusCodes.LevelError, RtmpStatusCodes.PublishBadName, description);
            _closeRequested = true;
        }

        private async ValueTask HandleMediaAsync(RtmpMessage message)
        {
            if (message.Type == RtmpMessageType.DataAmf0)
            {
                _tags.ProcessTag((int)FlvTagType.ScriptData, filtered: false, message.Timestamp, message.Payload.Span);
                _ingest!.SetMetadata(_tags.Metadata);
                if (!_tracksDecided && _tags.Metadata is { } metadata)
                {
                    DecideTracks(metadata.Find("audiocodecid") is not null, metadata.Find("videocodecid") is not null);
                }

                _timeout.CancelAfter(_options.IdleTimeout);
                return;
            }

            if (!_tracksDecided)
            {
                DecideTracks(audio: true, video: true);
            }

            _tags.ProcessTag((int)message.Type, filtered: false, message.Timestamp, message.Payload.Span);
            if (_produced.Count == 0)
            {
                _timeout.CancelAfter(_options.IdleTimeout);
                return;
            }

            // Waiting on the consumer is the server's own stall, not the publisher's: the idle timer
            // pauses and the stall timer runs instead.
            _timeout.CancelAfter(Timeout.InfiniteTimeSpan);
            _stall.CancelAfter(_options.ConsumerStallTimeout);
            while (_produced.TryPeek(out var entry))
            {
                await _ingest!.EnqueueAsync(entry.Tracks, entry.Packet, _stall.Token).ConfigureAwait(false);
                _produced.Dequeue();
            }

            _stall.CancelAfter(Timeout.InfiniteTimeSpan);
            _timeout.CancelAfter(_options.IdleTimeout);
        }

        // Metadata naming neither codec says nothing about the tracks, so both are expected.
        private void DecideTracks(bool audio, bool video)
        {
            _tracksDecided = true;
            _tags.ExpectTracks(audio || !video, video || !audio);
        }

        // An ingest connection has nothing left to do once its one publish is over.
        private void CloseAfterPublish()
        {
            if (_ingest is not null)
            {
                EndPublish(null);
                _closeRequested = true;
            }
        }

        private void EndPublish(Exception? error)
        {
            if (_ingest is null || _publishEnded)
            {
                return;
            }

            _publishEnded = true;
            if (error is null)
            {
                // Packets still held for a missing sequence header, and any a stalled enqueue left
                // behind, go to the consumer before the end (bounded by FlvDemuxer.MaxPendingPackets).
                _tags.Complete();
                while (_produced.TryDequeue(out var entry))
                {
                    _ingest.EnqueueUnbounded(entry.Tracks, entry.Packet);
                }
            }

            _produced.Clear();
            _ingest.End(error);
            _server.ReleasePublish(_ingest.AppName, _ingest.StreamKey);
            _server.RaisePublishEnded(_ingest);
        }

        private void ReplyIfExpected(RtmpCommand command)
        {
            if (command.TransactionId > 0)
            {
                WriteCommand(RtmpCommand.Result(command.TransactionId, null, Amf0Value.Undefined));
            }
        }

        private void WriteCommand(RtmpCommand command) => _writer.Write(_output, CommandChunkStreamId, command.ToMessage());

        private void WriteStatus(uint streamId, string level, string code, string description) =>
            _writer.Write(_output, StatusChunkStreamId, RtmpCommand.OnStatus(level, code, description).ToMessage(streamId));

        private async ValueTask FlushAsync(Stream stream)
        {
            if (_output.WrittenCount == 0)
            {
                return;
            }

            await stream.WriteAsync(_output.WrittenMemory, _timeout.Token).ConfigureAwait(false);
            _output.ResetWrittenCount();
        }

        private bool IsValidName(string? name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > _options.MaxNameLength)
            {
                return false;
            }

            foreach (var c in name)
            {
                if (char.IsControl(c))
                {
                    return false;
                }
            }

            return true;
        }

        private static string Truncate(string value) => value.Length <= 64 ? value : value[..64];

        private static EndPoint? TryGetRemoteEndPoint(Socket socket)
        {
            try
            {
                return socket.RemoteEndPoint;
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                return null;
            }
        }
    }
}
