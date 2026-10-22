using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace EggEncoder.Live.Rtmp
{
    /// <summary>
    /// An RTMP ingest server: OBS, vMix, ffmpeg or a hardware encoder publishes to
    /// <c>rtmp://host:port/app/streamKey</c> and each authorized publish arrives as an
    /// <see cref="RtmpIngestStream"/> of packets, through <see cref="PublishStarted"/>. RTMPS (RTMP over
    /// TLS) when <see cref="RtmpServerOptions.Certificate"/> is set. Ingest only: <c>play</c> is
    /// refused. See docs/live-ingest-restream-plan.md item 4.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The defaults are deliberately closed (docs/live-ingest-restream-plan.md "Security posture"):
    /// listen on <c>127.0.0.1</c> only, refuse every publish until
    /// <see cref="RtmpServerOptions.AuthorizePublishAsync"/> is set, at most one publisher per
    /// application/stream key, bounded connection count, chunk and message sizes, AMF0 nesting and name
    /// lengths, and a timeout on the handshake, on getting to <c>publish</c>, on an idle publisher and
    /// on a consumer that stops reading. A connection that breaks a bound is closed; other connections
    /// carry on.
    /// </para>
    /// <para>
    /// The events are raised on the connection's own task: a handler should hand the stream off (start
    /// reading it on another task) rather than block. An exception from a
    /// <see cref="PublishStarted"/> handler disconnects that publisher.
    /// </para>
    /// </remarks>
    public sealed class RtmpServer : IAsyncDisposable
    {
        private static readonly TimeSpan AcceptRetryDelay = TimeSpan.FromMilliseconds(50);

        private readonly object _gate = new();
        private readonly HashSet<(string App, string Key)> _liveStreams = [];
        private readonly ConcurrentDictionary<RtmpServerConnection, Task> _connections = new();
        private Socket? _listener;
        private CancellationTokenSource? _stopping;
        private Task? _acceptLoop;
        private int _connectionCount;
        private bool _stopped;

        /// <param name="options">The configuration; <c>null</c> for the defaults (which accept no publish).</param>
        public RtmpServer(RtmpServerOptions? options = null)
        {
            Options = options ?? new RtmpServerOptions();
        }

        /// <summary>Raised when a publish has been authorized, before its media arrives.</summary>
        public event Action<RtmpIngestStream>? PublishStarted;

        /// <summary>Raised once a publish has ended (see <see cref="RtmpIngestStream"/>) and its packet stream is complete.</summary>
        public event Action<RtmpIngestStream>? PublishEnded;

        /// <summary>The configuration this server runs with.</summary>
        public RtmpServerOptions Options { get; }

        /// <summary>The address actually listened on (the real port when <see cref="RtmpServerOptions.EndPoint"/> asked for port 0); <c>null</c> before <see cref="StartAsync"/>.</summary>
        public IPEndPoint? LocalEndPoint { get; private set; }

        /// <summary>Connections currently open.</summary>
        public int ConnectionCount => Volatile.Read(ref _connectionCount);

        /// <summary>Binds the listening socket and starts accepting connections.</summary>
        /// <exception cref="InvalidOperationException">Already started, or stopped.</exception>
        /// <exception cref="SocketException">The address can't be bound (in use, not local).</exception>
        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_stopped || _listener is not null)
                {
                    throw new InvalidOperationException(_stopped ? "The server has been stopped" : "The server has already started");
                }

                var endPoint = Options.EndPoint;
                var listener = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    if (endPoint.AddressFamily == AddressFamily.InterNetworkV6 && endPoint.Address.Equals(IPAddress.IPv6Any))
                    {
                        listener.DualMode = true;
                    }

                    listener.Bind(endPoint);
                    listener.Listen(Options.MaxConnections);
                }
                catch
                {
                    listener.Dispose();
                    throw;
                }

                _listener = listener;
                LocalEndPoint = (IPEndPoint)listener.LocalEndPoint!;
                _stopping = new CancellationTokenSource();
                _acceptLoop = AcceptLoopAsync(listener, _stopping.Token);
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Stops listening, disconnects every connection (ending their publishes normally) and waits for
        /// them to finish. Calling it again, or before <see cref="StartAsync"/>, does nothing more.
        /// </summary>
        public async Task StopAsync()
        {
            Task? acceptLoop;
            lock (_gate)
            {
                if (_stopped)
                {
                    acceptLoop = _acceptLoop;
                }
                else
                {
                    _stopped = true;
                    acceptLoop = _acceptLoop;
                    _stopping?.Cancel();
                    _listener?.Dispose();
                }
            }

            if (acceptLoop is not null)
            {
                await acceptLoop.ConfigureAwait(false);
            }

            await Task.WhenAll(_connections.Values).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            _stopping?.Dispose();
        }

        internal bool TryReservePublish(string app, string key)
        {
            lock (_gate)
            {
                return !_stopped && _liveStreams.Add((app, key));
            }
        }

        internal void ReleasePublish(string app, string key)
        {
            lock (_gate)
            {
                _liveStreams.Remove((app, key));
            }
        }

        internal void RaisePublishStarted(RtmpIngestStream stream) => PublishStarted?.Invoke(stream);

        // An ended publish must not take the connection task down with it, whatever a handler does.
        internal void RaisePublishEnded(RtmpIngestStream stream)
        {
            try
            {
                PublishEnded?.Invoke(stream);
            }
            catch (Exception)
            {
                // The publish is already over; there is nothing left to undo.
            }
        }

        private async Task AcceptLoopAsync(Socket listener, CancellationToken cancellationToken)
        {
            while (true)
            {
                Socket socket;
                try
                {
                    socket = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException
                    || (e is SocketException && cancellationToken.IsCancellationRequested))
                {
                    return;
                }
                catch (SocketException)
                {
                    // A connection reset before it was accepted, or the process is out of sockets for
                    // a moment: keep listening, without spinning on an error that repeats.
                    try
                    {
                        await Task.Delay(AcceptRetryDelay, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    continue;
                }

                if (Interlocked.Increment(ref _connectionCount) > Options.MaxConnections)
                {
                    Interlocked.Decrement(ref _connectionCount);
                    socket.Dispose();
                    continue;
                }

                // Registered before it runs, so StopAsync can't miss a connection that is just starting.
                var connection = new RtmpServerConnection(this, socket);
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _connections[connection] = done.Task;
                _ = RunConnectionAsync(connection, done, cancellationToken);
            }
        }

        private async Task RunConnectionAsync(RtmpServerConnection connection, TaskCompletionSource done, CancellationToken cancellationToken)
        {
            await Task.Yield();
            try
            {
                await connection.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _connectionCount);
                _connections.TryRemove(connection, out _);
                done.SetResult();
            }
        }
    }
}
