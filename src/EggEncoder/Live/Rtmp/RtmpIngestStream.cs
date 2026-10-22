using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using EggEncoder.Containers.Flv;
using EggEncoder.Live.Rtmp.Amf0;
using EggEncoder.Media;

namespace EggEncoder.Live.Rtmp
{
    /// <summary>
    /// One accepted publish on an <see cref="RtmpServer"/>: who is publishing, and the stream as
    /// packets. Delivered by <see cref="RtmpServer.PublishStarted"/> as soon as the publish is
    /// authorized, before any media has arrived. Video stays length-prefixed (AVCC/HVCC) and AAC a raw
    /// access unit, exactly as the publisher sent them, so packets can be remuxed without decoding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read the packets with <see cref="ReadPacketsAsync"/>, promptly: they wait in a bounded queue
    /// (<see cref="RtmpServerOptions.PacketQueueCapacity"/>, <see cref="RtmpServerOptions.PacketQueueMaxBytes"/>),
    /// and when it is full the server stops reading from the publisher; a consumer that stays away
    /// longer than <see cref="RtmpServerOptions.ConsumerStallTimeout"/> gets the publisher disconnected.
    /// </para>
    /// <para>
    /// The publish ends when the publisher unpublishes (<c>FCUnpublish</c>/<c>deleteStream</c>),
    /// disconnects, times out, or the server stops: the packet enumeration then finishes normally once
    /// every queued packet has been read, and <see cref="Completion"/> completes. Only a publisher that
    /// broke the protocol (or sent a malformed sequence header), or a <see cref="RtmpServer.PublishStarted"/>
    /// handler that threw, ends it with an exception, thrown from <see cref="ReadPacketsAsync"/> and
    /// carried by <see cref="Completion"/>. <see cref="Disconnect"/> ends it from the consumer's side.
    /// </para>
    /// <para>
    /// Tracks: the server expects the tracks the publisher's <c>onMetaData</c> names
    /// (<c>videocodecid</c>/<c>audiocodecid</c>) when it comes before the first media message, as it
    /// does from OBS and ffmpeg, and both otherwise; packets are held until every expected track has
    /// its sequence header, then follow the same rules as <see cref="FlvDemuxer"/> (track ids
    /// <see cref="FlvDemuxer.VideoTrackId"/>/<see cref="FlvDemuxer.AudioTrackId"/>, timescale 1000).
    /// </para>
    /// </remarks>
    public sealed class RtmpIngestStream
    {
        private readonly Channel<(MediaTrackSet? Tracks, MediaPacket Packet)> _queue =
            Channel.CreateUnbounded<(MediaTrackSet?, MediaPacket)>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        private readonly SemaphoreSlim _dequeued = new(0);
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Action _disconnect;
        private readonly int _capacity;
        private readonly long _maxBytes;
        private int _queuedCount;
        private long _queuedBytes;
        private volatile MediaTrackSet? _tracks;
        private volatile FlvMetadata? _metadata;
        private int _started;

        internal RtmpIngestStream(RtmpPublishRequest request, int capacity, long maxBytes, Action disconnect)
        {
            Request = request;
            _capacity = capacity;
            _maxBytes = maxBytes;
            _disconnect = disconnect;
        }

        /// <summary>The application name (<c>live</c> in <c>rtmp://host/live/key</c>).</summary>
        public string AppName => Request.AppName;

        /// <summary>The stream key (a secret: don't log it). See <see cref="RtmpPublishRequest.StreamKey"/>.</summary>
        public string StreamKey => Request.StreamKey;

        /// <summary>The publisher's address, when known.</summary>
        public EndPoint? RemoteEndPoint => Request.RemoteEndPoint;

        /// <summary>The publisher's <c>connect</c> command object properties.</summary>
        public IReadOnlyList<KeyValuePair<string, Amf0Value>> ConnectProperties => Request.ConnectProperties;

        /// <summary>The request <see cref="RtmpServerOptions.AuthorizePublishAsync"/> accepted.</summary>
        public RtmpPublishRequest Request { get; }

        /// <summary>
        /// The track set that describes the packet most recently yielded by <see cref="ReadPacketsAsync"/>:
        /// <c>null</c> until the first packet, then replaced, just before the first packet that uses it,
        /// whenever a track appears or its configuration changes (compare by reference to notice).
        /// </summary>
        public MediaTrackSet? Tracks => _tracks;

        /// <summary>The publisher's most recent <c>onMetaData</c>, or <c>null</c> if it sent none (yet).</summary>
        public FlvMetadata? Metadata => _metadata;

        /// <summary>
        /// Completes when the publish has ended and every packet was queued. Faults only on a protocol
        /// error (<see cref="RtmpProtocolException"/>) or with the exception a
        /// <see cref="RtmpServer.PublishStarted"/> handler threw.
        /// </summary>
        public Task Completion => _completion.Task;

        /// <summary>Yields every packet of the publish, in order, until it ends. Can be enumerated once.</summary>
        /// <exception cref="RtmpProtocolException">The publisher broke the protocol or sent malformed media configuration.</exception>
        /// <exception cref="Exception">Whatever a <see cref="RtmpServer.PublishStarted"/> handler threw, which also ends the publish.</exception>
        /// <exception cref="InvalidOperationException">Already enumerated.</exception>
        public async IAsyncEnumerable<MediaPacket> ReadPacketsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
            {
                throw new InvalidOperationException("ReadPacketsAsync can only be enumerated once");
            }

            var reader = _queue.Reader;
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var entry))
                {
                    Interlocked.Decrement(ref _queuedCount);
                    Interlocked.Add(ref _queuedBytes, -entry.Packet.Payload.Length);
                    if (_dequeued.CurrentCount == 0)
                    {
                        _dequeued.Release();
                    }

                    if (entry.Tracks is not null)
                    {
                        _tracks = entry.Tracks;
                        continue;
                    }

                    yield return entry.Packet;
                }
            }
        }

        /// <summary>Disconnects the publisher, ending the publish (a way to kick a stream). Does nothing once it has ended.</summary>
        public void Disconnect() => _disconnect();

        internal void SetMetadata(FlvMetadata? metadata) => _metadata = metadata;

        // Waits while the queue is over either bound (the consumer is behind); the token is the
        // connection's consumer-stall timeout. A single entry larger than the byte bound still goes
        // through once the queue is empty, so one big keyframe can't wedge the stream.
        internal async ValueTask EnqueueAsync(MediaTrackSet? tracks, MediaPacket packet, CancellationToken cancellationToken)
        {
            var length = packet.Payload.Length;
            while (Volatile.Read(ref _queuedCount) >= _capacity
                || (Interlocked.Read(ref _queuedBytes) > 0 && Interlocked.Read(ref _queuedBytes) + length > _maxBytes))
            {
                await _dequeued.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            Interlocked.Increment(ref _queuedCount);
            Interlocked.Add(ref _queuedBytes, length);
            _queue.Writer.TryWrite((tracks, packet));
        }

        // The publish is over: whatever is left goes in regardless of the bounds, since nothing will
        // read from the publisher again (the caller bounds how much that is).
        internal void EnqueueUnbounded(MediaTrackSet? tracks, MediaPacket packet)
        {
            Interlocked.Increment(ref _queuedCount);
            Interlocked.Add(ref _queuedBytes, packet.Payload.Length);
            _queue.Writer.TryWrite((tracks, packet));
        }

        internal void End(Exception? error)
        {
            if (error is null)
            {
                _queue.Writer.TryComplete();
                _completion.TrySetResult();
            }
            else
            {
                _queue.Writer.TryComplete(error);
                _completion.TrySetException(error);
            }
        }
    }
}
