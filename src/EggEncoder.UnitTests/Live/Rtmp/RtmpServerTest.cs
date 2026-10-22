using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EggEncoder.Containers.Flv;
using EggEncoder.Live.Rtmp;
using EggEncoder.Live.Rtmp.Amf0;
using EggEncoder.Media;
using EggEncoder.UnitTests.Containers.Flv;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace EggEncoder.UnitTests.Live.Rtmp
{
    public class RtmpServerTest
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

        internal static RtmpServerOptions AcceptAll(RtmpServerOptions? options = null) => new()
        {
            EndPoint = options?.EndPoint ?? new IPEndPoint(IPAddress.Loopback, 0),
            AuthorizePublishAsync = options?.AuthorizePublishAsync ?? ((_, _) => ValueTask.FromResult(true)),
            Certificate = options?.Certificate,
            ChunkReader = options?.ChunkReader ?? new RtmpChunkReaderOptions(),
            TimeProvider = options?.TimeProvider ?? TimeProvider.System,
            MaxConnections = options?.MaxConnections ?? 64,
            MaxNameLength = options?.MaxNameLength ?? 256,
            PacketQueueCapacity = options?.PacketQueueCapacity ?? 1024,
            PacketQueueMaxBytes = options?.PacketQueueMaxBytes ?? 64 * 1024 * 1024
        };

        internal static async Task<RtmpServer> StartAsync(RtmpServerOptions options)
        {
            var server = new RtmpServer(options);
            await server.StartAsync();
            return server;
        }

        // Starts reading a publish's packets as soon as it starts, the way a real consumer would.
        internal sealed class Consumer
        {
            private readonly TaskCompletionSource<RtmpIngestStream> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<RtmpIngestStream> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private Task<List<MediaPacket>>? _reading;

            public Consumer(RtmpServer server, bool read = true)
            {
                server.PublishStarted += stream =>
                {
                    if (read)
                    {
                        _reading = ReadAllAsync(stream);
                    }

                    _started.TrySetResult(stream);
                };
                server.PublishEnded += stream => _ended.TrySetResult(stream);
            }

            public Task<RtmpIngestStream> Started => _started.Task.WaitAsync(TestTimeout);

            public Task<RtmpIngestStream> Ended => _ended.Task.WaitAsync(TestTimeout);

            public bool HasStarted => _started.Task.IsCompleted;

            public async Task<List<MediaPacket>> PacketsAsync()
            {
                await Started;
                return await _reading!.WaitAsync(TestTimeout);
            }

            private static async Task<List<MediaPacket>> ReadAllAsync(RtmpIngestStream stream)
            {
                var packets = new List<MediaPacket>();
                await foreach (var packet in stream.ReadPacketsAsync())
                {
                    packets.Add(packet);
                }

                return packets;
            }
        }

        internal static void AssertSamePackets(IReadOnlyList<MediaPacket> actual, IReadOnlyList<MediaPacket> expected)
        {
            actual.Should().HaveCount(expected.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                actual[i].TrackId.Should().Be(expected[i].TrackId);
                actual[i].Pts.Should().Be(expected[i].Pts);
                actual[i].Dts.Should().Be(expected[i].Dts);
                actual[i].IsKeyframe.Should().Be(expected[i].IsKeyframe);
                actual[i].ConfigVersion.Should().Be(expected[i].ConfigVersion);
                actual[i].Payload.ToArray().Should().Equal(expected[i].Payload.ToArray());
            }
        }

        [Fact]
        public async Task Publish_FlvFixture_OverLoopback_Should_Deliver_Exactly_The_Fixture_Packets()
        {
            await using var server = await StartAsync(AcceptAll());
            var consumer = new Consumer(server);
            var flv = File.ReadAllBytes(FlvDemuxerTest.H264AacFixture);
            var expected = FlvDemuxerTest.Demux(flv);

            var (publisher, status) = await RtmpTestPublisher.PublishAsync(server.LocalEndPoint!, "live", "abc123");
            await using (publisher)
            {
                status.StatusCode.Should().Be(RtmpStatusCodes.PublishStart);
                var stream = await consumer.Started;
                stream.AppName.Should().Be("live");
                stream.StreamKey.Should().Be("abc123");
                stream.RemoteEndPoint.Should().BeOfType<IPEndPoint>().Which.Address.Should().Be(IPAddress.Loopback);
                stream.Request.PublishingType.Should().Be("live");
                stream.Request.TcUrl.Should().Be("rtmp://127.0.0.1/live");
                stream.Request.FlashVersion.Should().Be(RtmpCommand.DefaultFlashVersion);
                stream.ConnectProperties.Should().Contain(p => p.Key == "app");

                await publisher.SendFlvAsync(flv);
                await publisher.UnpublishAsync("abc123");

                var packets = await consumer.PacketsAsync();
                AssertSamePackets(packets, expected.Packets);
                stream.Tracks!.Tracks.Should().HaveCount(2);
                stream.Tracks.Tracks.Select(t => t.Codec).Should().Equal(MediaCodec.H264, MediaCodec.Aac);
                stream.Tracks.Tracks[0].Width.Should().Be(64);
                stream.Metadata!.Width.Should().Be(64);
                (await consumer.Ended).Should().BeSameAs(stream);
                await stream.Completion.WaitAsync(TestTimeout);
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(1500)]
        [InlineData(int.MaxValue)]
        public async Task Publish_ReplayedFfmpegCapture_Should_Deliver_Exactly_The_Fixture_Packets(int writeSize)
        {
            // Every byte ffmpeg 9.0.2 sent publishing h264_aac.flv (RtmpFfmpegCaptureTest), replayed:
            // a real client's connect/releaseStream/FCPublish/createStream/publish and @setDataFrame.
            await using var server = await StartAsync(AcceptAll());
            var consumer = new Consumer(server);
            var capture = File.ReadAllBytes(RtmpFfmpegCaptureTest.Fixture);
            const int C0C1 = 1 + RtmpHandshake.PacketLength;

            using var client = new TcpClient();
            await client.ConnectAsync(server.LocalEndPoint!);
            var network = client.GetStream();
            await network.WriteAsync(capture.AsMemory(0, C0C1));
            var s0s1s2 = new byte[1 + (2 * RtmpHandshake.PacketLength)];
            await network.ReadExactlyAsync(s0s1s2);
            s0s1s2[0].Should().Be(RtmpHandshake.Version);
            for (var offset = C0C1; offset < capture.Length;)
            {
                var length = Math.Min(writeSize, capture.Length - offset);
                await network.WriteAsync(capture.AsMemory(offset, length));
                offset += length;
            }

            var stream = await consumer.Started;
            stream.StreamKey.Should().Be("streamkey");
            var packets = await consumer.PacketsAsync();
            AssertSamePackets(packets, FlvDemuxerTest.Demux(File.ReadAllBytes(FlvDemuxerTest.H264AacFixture)).Packets);
            stream.Metadata!.Encoder.Should().StartWith("Lavf");
        }

        [Fact]
        public async Task Publish_WithNoAuthorizationCallback_Should_Be_Rejected_With_BadName()
        {
            await using var server = await StartAsync(new RtmpServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) });
            var consumer = new Consumer(server);

            var (publisher, status) = await RtmpTestPublisher.PublishAsync(server.LocalEndPoint!);
            await using (publisher)
            {
                status.StatusCode.Should().Be(RtmpStatusCodes.PublishBadName);
                status.Arguments[0].GetProperty("level")!.String.Should().Be(RtmpStatusCodes.LevelError);
                (await publisher.WaitForCloseAsync()).Should().BeTrue();
                consumer.HasStarted.Should().BeFalse();
            }
        }

        [Fact]
        public async Task Publish_RejectedByCallback_Should_Get_BadName_And_See_The_Request()
        {
            RtmpPublishRequest? seen = null;
            await using var server = await StartAsync(AcceptAll(new RtmpServerOptions
            {
                AuthorizePublishAsync = (request, _) =>
                {
                    seen = request;
                    return ValueTask.FromResult(request.StreamKey == "good");
                }
            }));
            var consumer = new Consumer(server);

            var (rejected, status) = await RtmpTestPublisher.PublishAsync(server.LocalEndPoint!, "app1", "bad");
            await using (rejected)
            {
                status.StatusCode.Should().Be(RtmpStatusCodes.PublishBadName);
                (await rejected.WaitForCloseAsync()).Should().BeTrue();
            }

            seen!.AppName.Should().Be("app1");
            seen.StreamKey.Should().Be("bad");
            seen.ToString().Should().Contain("app1").And.NotContain("bad");
            consumer.HasStarted.Should().BeFalse();

            // The rejected key was released, and a different one is accepted.
            var (accepted, ok) = await RtmpTestPublisher.PublishAsync(server.LocalEndPoint!, "app1", "good");
            await using (accepted)
            {
                ok.StatusCode.Should().Be(RtmpStatusCodes.PublishStart);
            }
        }

        [Fact]
        public async Task Publish_WhenCallbackThrows_Should_Close_The_Connection_And_Release_The_Key()
        {
            var calls = 0;
            await using var server = await StartAsync(AcceptAll(new RtmpServerOptions
            {
                AuthorizePublishAsync = (_, _) => Interlocked.Increment(ref calls) == 1
                    ? throw new InvalidOperationException("auth backend down")
                    : ValueTask.FromResult(true)
            }));

            await using (var publisher = await RtmpTestPublisher.OpenAsync(server.LocalEndPoint!))
            {
                await publisher.HandshakeAsync();
                await publisher.ConnectAsync("live");
                await publisher.PublishAsync(await publisher.CreateStreamAsync(), "streamkey")
                    .Awaiting(t => t).Should().ThrowAsync<EndOfStreamException>();
            }

            var (again, status) = await RtmpTestPublisher.PublishAsync(server.LocalEndPoint!);
            await using (again)
            {
                status.StatusCode.Should().Be(RtmpStatusCodes.PublishStart);
            }
        }

        [Fact]
        public async Task Publish_OfAKeyAlreadyLive_Should_Be_Rejected_Until_The_First_Publish_Ends()
        {
            await using var server = await StartAsync(AcceptAll());
            var consumer = new Consumer(server);

            var (first, firstStatus) = await RtmpTestPublisher.PublishAsync(server.LocalEndPoint!);
            firstStatus.StatusCode.Should().Be(RtmpStatusCodes.PublishStart);

            var (second, secondStatus) = await RtmpTestPublisher.PublishAsync(server.LocalEndPoint!);
            await using (second)
            {
                secondStatus.StatusCode.Should().Be(RtmpStatusCodes.PublishBadName);
            }

            // The same key under another application is a different stream.
            var (otherApp, otherStatus) = await RtmpTestPublisher.PublishAsync(server.LocalEndPoint!, "other");
            await using (otherApp)
            {
                otherStatus.StatusCode.Should().Be(RtmpStatusCodes.PublishStart);
            }

            await first.UnpublishAsync();
            await consumer.Ended;
            await first.DisposeAsync();

            var (third, thirdStatus) = await RtmpTestPublisher.PublishAsync(server.LocalEndPoint!);
            await using (third)
            {
                thirdStatus.StatusCode.Should().Be(RtmpStatusCodes.PublishStart);
            }
        }

        public static TheoryData<string> InvalidStreamKeys => new() { string.Empty, new string('k', 257), "key\nwith-newline" };

        [Theory]
        [MemberData(nameof(InvalidStreamKeys))]
        public async Task Publish_WithAnInvalidStreamKey_Should_Be_Rejected_Before_The_Callback(string key)
        {
            var called = false;
            await using var server = await StartAsync(AcceptAll(new RtmpServerOptions
            {
                AuthorizePublishAsync = (_, _) =>
                {
                    called = true;
                    return ValueTask.FromResult(true);
                }
            }));

            var (publisher, status) = await RtmpTestPublisher.PublishAsync(server.LocalEndPoint!, key: key);
            await using (publisher)
            {
                status.StatusCode.Should().Be(RtmpStatusCodes.PublishBadName);
                called.Should().BeFalse();
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("app\u0001")]
        public async Task Connect_WithAnInvalidAppName_Should_Be_Rejected(string app)
        {
            await using var server = await StartAsync(AcceptAll());
            await using var publisher = await RtmpTestPublisher.OpenAsync(server.LocalEndPoint!);
            await publisher.HandshakeAsync();

            var reply = await publisher.ConnectAsync(app);

            reply.Name.Should().Be("_error");
            reply.StatusCode.Should().Be(RtmpStatusCodes.ConnectRejected);
            (await publisher.WaitForCloseAsync()).Should().BeTrue();
        }

        [Fact]
        public async Task Connect_WithoutAnAppProperty_Should_Be_Rejected()
        {
            await using var server = await StartAsync(AcceptAll());
            await using var publisher = await RtmpTestPublisher.OpenAsync(server.LocalEndPoint!);
            await publisher.HandshakeAsync();

            await publisher.SendCommandAsync(new RtmpCommand("connect", 1, Amf0Value.FromObject([new("app", Amf0Value.FromNumber(1))])));

            (await publisher.WaitForReplyAsync(1)).StatusCode.Should().Be(RtmpStatusCodes.ConnectRejected);
        }

        [Fact]
        public async Task Command_BeforeConnect_Should_Close_The_Connection()
        {
            await using var server = await StartAsync(AcceptAll());
            await using var publisher = await RtmpTestPublisher.OpenAsync(server.LocalEndPoint!);
            await publisher.HandshakeAsync();

            await publisher.SendCommandAsync(RtmpCommand.CreateStream(1));

            (await publisher.WaitForCloseAsync()).Should().BeTrue();
            publisher.ReceivedCommands.Should().BeEmpty();
        }

        [Fact]
        public async Task Connect_Twice_Should_Close_The_Connection()
        {
            await using var server = await StartAsync(AcceptAll());
            await using var publisher = await RtmpTestPublisher.OpenAsync(server.LocalEndPoint!);
            await publisher.HandshakeAsync();
            await publisher.ConnectAsync("live");

            await publisher.SendCommandAsync(RtmpCommand.Connect(5, "live", "rtmp://127.0.0.1/live"));

            (await publisher.WaitForCloseAsync()).Should().BeTrue();
        }

        [Fact]
        public async Task Connect_Should_Announce_Window_Bandwidth_And_Chunk_Size()
        {
            await using var server = await StartAsync(AcceptAll());
            await using var publisher = await RtmpTestPublisher.OpenAsync(server.LocalEndPoint!);
            await publisher.HandshakeAsync();

            var reply = await publisher.ConnectAsync("live");

            reply.StatusCode.Should().Be(RtmpStatusCodes.ConnectSuccess);
            publisher.Received.Where(m => m.Type == RtmpMessageType.WindowAcknowledgementSize)
                .Select(m => RtmpProtocolControl.ReadWindowAcknowledgementSize(m.Payload.Span)).Should().Equal(2_500_000u);
            publisher.Received.Should().Contain(m => m.Type == RtmpMessageType.SetPeerBandwidth);
            publisher.Received.Where(m => m.Type == RtmpMessageType.SetChunkSize)
                .Select(m => RtmpProtocolControl.ReadSetChunkSize(m.Payload.Span)).Should().Equal(4096);
        }

        [Fact]
        public async Task Publish_OnAStreamThatWasNeverCreated_Should_Be_Rejected()
        {
            await using var server = await StartAsync(AcceptAll());
            await using var publisher = await RtmpTestPublisher.OpenAsync(server.LocalEndPoint!);
            await publisher.HandshakeAsync();
            await publisher.ConnectAsync("live");

            var status = await publisher.PublishAsync(streamId: 7, "streamkey");

            status.StatusCode.Should().Be(RtmpStatusCodes.PublishBadName);
        }

        [Fact]
        public async Task Publish_Twice_On_One_Connection_Should_Reject_The_Second()
        {
            await using var server = await StartAsync(AcceptAll());
            var consumer = new Consumer(server);
            var (publisher, status) = await RtmpTestPublisher.PublishAsync(server.LocalEndPoint!);
            await using (publisher)
            {
                status.StatusCode.Should().Be(RtmpStatusCodes.PublishStart);
                var second = await publisher.CreateStreamAsync();

                await publisher.SendCommandAsync(RtmpCommand.Publish("another"), second, chunkStreamId: 8);

                await publisher.WaitForAsync(() => publisher.ReceivedCommands.FirstOrDefault(c => c.StatusCode == RtmpStatusCodes.PublishBadName));
                (await publisher.WaitForCloseAsync()).Should().BeTrue();
                await consumer.Ended;
            }
        }

        [Fact]
        public async Task Play_Should_Be_Refused_And_The_Connection_Closed()
        {
            await using var server = await StartAsync(AcceptAll());
            await using var publisher = await RtmpTestPublisher.OpenAsync(server.LocalEndPoint!);
            await publisher.HandshakeAsync();
            await publisher.ConnectAsync("live");
            var streamId = await publisher.CreateStreamAsync();

            await publisher.SendCommandAsync(RtmpCommand.Play("streamkey"), streamId, chunkStreamId: 8);

            var status = await publisher.WaitForAsync(() => publisher.ReceivedCommands.FirstOrDefault(c => c.Name == "onStatus"));
            status.StatusCode.Should().Be(RtmpStatusCodes.PlayFailed);
            (await publisher.WaitForCloseAsync()).Should().BeTrue();
        }

        [Fact]
        public async Task Calls_Should_Get_Result_For_The_Publish_Prelude_And_Error_For_Unknown_Ones()
        {
            await using var server = await StartAsync(AcceptAll());
            await using var publisher = await RtmpTestPublisher.OpenAsync(server.LocalEndPoint!);
            await publisher.HandshakeAsync();
            await publisher.ConnectAsync("live");

            await publisher.SendCommandAsync(new RtmpCommand("getStreamLength", 10, null, Amf0Value.FromString("x")));
            await publisher.SendCommandAsync(new RtmpCommand("noSuchCall", 11, null));
            await publisher.SendCommandAsync(new RtmpCommand("noReplyExpected", 0, null));
            await publisher.SendCommandAsync(RtmpCommand.FCUnpublish(12, "nothing-live"));

            (await publisher.WaitForReplyAsync(10)).Name.Should().Be("_result");
            var error = await publisher.WaitForReplyAsync(11);
            error.Name.Should().Be("_error");
            error.StatusCode.Should().Be(RtmpStatusCodes.CallFailed);
            (await publisher.WaitForReplyAsync(12)).Name.Should().Be("_result");
            publisher.ReceivedCommands.Should().NotContain(c => c.TransactionId == 0 && c.Name == "_error");
        }

        [Fact]
        public async Task PingRequest_Should_Get_A_PingResponse()
        {
            await using var server = await StartAsync(AcceptAll());
            await using var publisher = await RtmpTestPublisher.OpenAsync(server.LocalEndPoint!);
            await publisher.HandshakeAsync();
            await publisher.ConnectAsync("live");

            await publisher.SendAsync(RtmpUserControl.PingRequest(1234).ToMessage(), RtmpProtocolControl.ChunkStreamId);
            await publisher.SendAsync(RtmpUserControl.SetBufferLength(1, 3000).ToMessage(), RtmpProtocolControl.ChunkStreamId);

            var pong = await publisher.WaitForAsync(() => publisher.Received
                .Where(m => m.Type == RtmpMessageType.UserControl)
                .Select(m => (RtmpUserControl?)RtmpUserControl.Read(m.Payload.Span))
                .FirstOrDefault(c => c!.Value.EventType == RtmpUserControlEventType.PingResponse) is { } found ? new[] { found } : null);
            pong[0].Value.Should().Be(1234);
        }

        [Fact]
        public async Task Publisher_WindowAcknowledgementSize_Should_Be_Acknowledged_By_The_Server()
        {
            await using var server = await StartAsync(AcceptAll());
            var consumer = new Consumer(server);
            var (publisher, _) = await RtmpTestPublisher.PublishAsync(server.LocalEndPoint!);
            await using (publisher)
            {
                await publisher.SendAsync(RtmpProtocolControl.CreateWindowAcknowledgementSize(1000), RtmpProtocolControl.ChunkStreamId);
                await publisher.SendFlvAsync(File.ReadAllBytes(FlvDemuxerTest.H264AacFixture));

                var ack = await publisher.WaitForAsync(() => publisher.Received.FirstOrDefault(m => m.Type == RtmpMessageType.Acknowledgement) is { Payload.Length: 4 } m ? new[] { m } : null);
                RtmpProtocolControl.ReadAcknowledgement(ack[0].Payload.Span).Should().BeGreaterThanOrEqualTo(1000);
            }
        }

        [Fact]
        public async Task Media_BeforePublish_Or_On_Another_Stream_Should_Be_Ignored()
        {
            await using var server = await StartAsync(AcceptAll());
            var consumer = new Consumer(server);
            await using var publisher = await RtmpTestPublisher.OpenAsync(server.LocalEndPoint!);
            await publisher.HandshakeAsync();
            await publisher.ConnectAsync("live");
            var streamId = await publisher.CreateStreamAsync();
            byte[] avcHeader = [0x17, 0, 0, 0, 0, .. FlvTestBuilder.AvcC];
            await publisher.SendAsync(new RtmpMessage(RtmpMessageType.Video, streamId, 0, avcHeader));

            await publisher.PublishAsync(streamId, "streamkey");
            await publisher.SendAsync(new RtmpMessage(RtmpMessageType.Video, streamId + 5, 0, avcHeader));
            await publisher.SendAsync(new RtmpMessage(RtmpMessageType.DataAmf3, streamId, 0, new byte[] { 0 }));
            await publisher.SendAsync(new RtmpMessage(RtmpMessageType.Aggregate, streamId, 0, new byte[] { 0 }));
            await publisher.UnpublishAsync();

            (await consumer.PacketsAsync()).Should().BeEmpty();
            (await consumer.Started).Tracks.Should().BeNull();
        }

        [Fact]
        public async Task Amf3Command_Should_Close_The_Connection()
        {
            await using var server = await StartAsync(AcceptAll());
            await using var publisher = await RtmpTestPublisher.OpenAsync(server.LocalEndPoint!);
            await publisher.HandshakeAsync();

            await publisher.SendAsync(new RtmpMessage(RtmpMessageType.CommandAmf3, 0, 0, new byte[] { 0, 2, 0, 1, (byte)'x' }));

            (await publisher.WaitForCloseAsync()).Should().BeTrue();
        }

        [Fact]
        public async Task Publisher_Disconnecting_MidStream_Should_End_The_Publish_Normally_With_What_Arrived()
        {
            await using var server = await StartAsync(AcceptAll());
            var consumer = new Consumer(server);
            var flv = File.ReadAllBytes(FlvDemuxerTest.H264AacFixture);
            var (publisher, _) = await RtmpTestPublisher.PublishAsync(server.LocalEndPoint!);
            await publisher.SendFlvAsync(flv);

            // Half a message, then the socket goes away without unpublishing.
            await publisher.SendRawAsync([0x06, 0, 0, 0, 0, 0x10, 0x00, 0x09, 1, 0, 0, 0, 0x17]);
            await publisher.DisposeAsync();

            var packets = await consumer.PacketsAsync();
            AssertSamePackets(packets, FlvDemuxerTest.Demux(flv).Packets);
            await (await consumer.Started).Completion.WaitAsync(TestTimeout);
        }

        [Fact]
        public async Task Publish_WithAMalformedSequenceHeader_Should_Fault_The_Stream()
        {
            await using var server = await StartAsync(AcceptAll());
            var consumer = new Consumer(server, read: false);
            var (publisher, _) = await RtmpTestPublisher.PublishAsync(server.LocalEndPoint!);
            await using (publisher)
            {
                // AVC sequence header whose avcC is a single byte.
                await publisher.SendAsync(new RtmpMessage(RtmpMessageType.Video, publisher.StreamId, 0, new byte[] { 0x17, 0, 0, 0, 0, 1 }));

                var stream = await consumer.Started;
                await stream.Completion.Awaiting(t => t.WaitAsync(TestTimeout)).Should().ThrowAsync<RtmpProtocolException>();
                var read = async () =>
                {
                    await foreach (var _ in stream.ReadPacketsAsync())
                    {
                    }
                };
                await read.Should().ThrowAsync<RtmpProtocolException>();
                (await publisher.WaitForCloseAsync()).Should().BeTrue();
            }
        }

        [Fact]
        public async Task IngestStream_Disposed_Should_Disconnect_The_Publisher()
        {
            await using var server = await StartAsync(AcceptAll());
            var consumer = new Consumer(server);
            var (publisher, _) = await RtmpTestPublisher.PublishAsync(server.LocalEndPoint!);
            await using (publisher)
            {
                var stream = await consumer.Started;

                await stream.DisposeAsync();

                (await publisher.WaitForCloseAsync()).Should().BeTrue();
                await stream.Completion.WaitAsync(TestTimeout);
                (await consumer.PacketsAsync()).Should().BeEmpty();
            }
        }

        [Fact]
        public async Task IngestStream_ReadPacketsAsync_Twice_Should_Throw()
        {
            await using var server = await StartAsync(AcceptAll());
            var consumer = new Consumer(server);
            var (publisher, _) = await RtmpTestPublisher.PublishAsync(server.LocalEndPoint!);
            await using (publisher)
            {
                var stream = await consumer.Started;
                var again = async () =>
                {
                    await foreach (var _ in stream.ReadPacketsAsync())
                    {
                    }
                };

                await again.Should().ThrowAsync<InvalidOperationException>();
            }
        }

        [Fact]
        public async Task PublishStarted_HandlerThrowing_Should_Disconnect_And_Fault_The_Stream()
        {
            await using var server = await StartAsync(AcceptAll());
            var started = new TaskCompletionSource<RtmpIngestStream>();
            server.PublishStarted += stream =>
            {
                started.TrySetResult(stream);
                throw new InvalidOperationException("handler bug");
            };
            server.PublishEnded += _ => throw new InvalidOperationException("ended handler bug, swallowed");

            await using var publisher = await RtmpTestPublisher.OpenAsync(server.LocalEndPoint!);
            await publisher.HandshakeAsync();
            await publisher.ConnectAsync("live");
            await publisher.PublishAsync(await publisher.CreateStreamAsync(), "streamkey").Awaiting(t => t).Should().ThrowAsync<EndOfStreamException>();

            var stream = await started.Task.WaitAsync(TestTimeout);
            await stream.Completion.Awaiting(t => t.WaitAsync(TestTimeout)).Should().ThrowAsync<InvalidOperationException>().WithMessage("handler bug");
        }

        [Fact]
        public async Task StopAsync_WhilePublishing_Should_End_The_Publish_Normally()
        {
            var server = await StartAsync(AcceptAll());
            var consumer = new Consumer(server);
            var (publisher, _) = await RtmpTestPublisher.PublishAsync(server.LocalEndPoint!);
            await using (publisher)
            {
                var stream = await consumer.Started;

                await server.StopAsync().WaitAsync(TestTimeout);

                await stream.Completion.WaitAsync(TestTimeout);
                server.ConnectionCount.Should().Be(0);
                (await publisher.WaitForCloseAsync()).Should().BeTrue();
                await server.DisposeAsync();
            }
        }

        [Fact]
        public async Task Lifecycle_Should_Reject_Starting_Twice_Or_After_Stop()
        {
            var server = new RtmpServer(AcceptAll());
            server.LocalEndPoint.Should().BeNull();
            await server.StopAsync();
            await server.StopAsync();
            await server.Invoking(s => s.StartAsync()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*stopped*");

            await using var running = new RtmpServer(AcceptAll());
            await running.StartAsync();
            running.LocalEndPoint!.Port.Should().BePositive();
            await running.Invoking(s => s.StartAsync()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*already started*");
            await running.Invoking(s => s.StartAsync(new CancellationToken(canceled: true))).Should().ThrowAsync<OperationCanceledException>();
        }

        [Fact]
        public async Task StartAsync_OnAPortInUse_Should_Throw_And_Stay_Unstarted()
        {
            await using var first = await StartAsync(AcceptAll());
            await using var second = new RtmpServer(AcceptAll(new RtmpServerOptions { EndPoint = first.LocalEndPoint! }));

            await second.Invoking(s => s.StartAsync()).Should().ThrowAsync<SocketException>();
            second.LocalEndPoint.Should().BeNull();
        }

        [Fact]
        public async Task StartAsync_OnIPv6Any_Should_Accept_IPv4_Loopback_Publishers()
        {
            await using var server = await StartAsync(AcceptAll(new RtmpServerOptions { EndPoint = new IPEndPoint(IPAddress.IPv6Any, 0) }));

            var (publisher, status) = await RtmpTestPublisher.PublishAsync(new IPEndPoint(IPAddress.Loopback, server.LocalEndPoint!.Port));
            await using (publisher)
            {
                status.StatusCode.Should().Be(RtmpStatusCodes.PublishStart);
            }
        }

        [Fact]
        public async Task Connections_OverTheLimit_Should_Be_Closed_While_Others_Keep_Working()
        {
            await using var server = await StartAsync(AcceptAll(new RtmpServerOptions { MaxConnections = 1 }));
            var consumer = new Consumer(server);
            var (first, status) = await RtmpTestPublisher.PublishAsync(server.LocalEndPoint!);
            await using (first)
            {
                status.StatusCode.Should().Be(RtmpStatusCodes.PublishStart);
                await using (var extra = await RtmpTestPublisher.OpenAsync(server.LocalEndPoint!))
                {
                    (await extra.WaitForCloseAsync()).Should().BeTrue();
                }

                var flv = File.ReadAllBytes(FlvDemuxerTest.H264AacFixture);
                await first.SendFlvAsync(flv);
                await first.UnpublishAsync();
                (await consumer.PacketsAsync()).Should().HaveCount(FlvDemuxerTest.Demux(flv).Packets.Count);
            }
        }

        public static TheoryData<string, byte[]> ViolatingChunkStreams => new()
        {
            // Set Chunk Size past MaxInboundChunkSize (65,536 by default).
            { "chunk size", [0x02, 0, 0, 0, 0, 0, 4, 1, 0, 0, 0, 0, 0x7F, 0xFF, 0xFF, 0xFF] },
            // A message claiming 9 MiB, over MaxMessageLength (8 MiB).
            { "message length", [0x06, 0, 0, 0, 0x90, 0x00, 0x00, 9, 1, 0, 0, 0] },
            // A type 1 header on a chunk stream that never had a type 0 one.
            { "never-started chunk stream", [0x47, 0, 0, 0, 0, 0, 4, 9] },
            // A connect whose command object nests past the AMF0 depth limit.
            { "amf0 nesting", ConnectWithNesting(40) },
            // Random bytes.
            { "random bytes", RandomBytes(4096, seed: 7) }
        };

        [Theory]
        [MemberData(nameof(ViolatingChunkStreams))]
        public async Task Peer_BreakingABound_Should_Be_Closed_While_Other_Connections_Keep_Working(string bound, byte[] bytes)
        {
            await using var server = await StartAsync(AcceptAll());
            var consumer = new Consumer(server);
            var (good, _) = await RtmpTestPublisher.PublishAsync(server.LocalEndPoint!);
            await using (good)
            {
                await using (var bad = await RtmpTestPublisher.OpenAsync(server.LocalEndPoint!))
                {
                    await bad.HandshakeAsync();
                    await bad.SendRawAsync(bytes);
                    (await bad.WaitForCloseAsync()).Should().BeTrue(bound);
                }

                var flv = File.ReadAllBytes(FlvDemuxerTest.H264AacFixture);
                await good.SendFlvAsync(flv);
                await good.UnpublishAsync();
                (await consumer.PacketsAsync()).Should().HaveCount(FlvDemuxerTest.Demux(flv).Packets.Count);
            }
        }

        [Theory]
        [InlineData(6)]
        [InlineData(8)]
        [InlineData(0)]
        public async Task Handshake_WithAnUnsupportedVersion_Should_Close_The_Connection(byte version)
        {
            await using var server = await StartAsync(AcceptAll());
            await using var publisher = await RtmpTestPublisher.OpenAsync(server.LocalEndPoint!);
            var c0c1 = new byte[1 + RtmpHandshake.PacketLength];
            c0c1[0] = version;

            await publisher.SendRawAsync(c0c1);

            (await publisher.WaitForCloseAsync()).Should().BeTrue();
        }

        [Fact]
        public async Task Rtmps_Publish_Should_Deliver_The_Fixture_Packets()
        {
            using var certificate = CreateTestCertificate();
            await using var server = await StartAsync(AcceptAll(new RtmpServerOptions { Certificate = certificate }));
            var consumer = new Consumer(server);
            var flv = File.ReadAllBytes(FlvDemuxerTest.H264AacFixture);

            var (publisher, status) = await RtmpTestPublisher.PublishAsync(server.LocalEndPoint!, tls: true);
            await using (publisher)
            {
                status.StatusCode.Should().Be(RtmpStatusCodes.PublishStart);
                await publisher.SendFlvAsync(flv);
                await publisher.UnpublishAsync();

                AssertSamePackets(await consumer.PacketsAsync(), FlvDemuxerTest.Demux(flv).Packets);
            }
        }

        [Fact]
        public async Task Rtmps_Server_Given_A_Plain_Rtmp_Client_Should_Close_The_Connection()
        {
            using var certificate = CreateTestCertificate();
            await using var server = await StartAsync(AcceptAll(new RtmpServerOptions { Certificate = certificate }));
            await using var publisher = await RtmpTestPublisher.OpenAsync(server.LocalEndPoint!);

            await publisher.SendRawAsync(new byte[1 + RtmpHandshake.PacketLength]);

            (await publisher.WaitForCloseAsync()).Should().BeTrue();
        }

        [Fact]
        public async Task Publish_AudioOnly_WithMetadata_Should_Announce_Only_The_Audio_Track()
        {
            await using var server = await StartAsync(AcceptAll());
            var consumer = new Consumer(server);
            var flv = File.ReadAllBytes(FlvDemuxerTest.Mp3Fixture);
            var (publisher, _) = await RtmpTestPublisher.PublishAsync(server.LocalEndPoint!);
            await using (publisher)
            {
                await publisher.SendFlvAsync(flv);
                var stream = await consumer.Started;
                await publisher.UnpublishAsync();

                AssertSamePackets(await consumer.PacketsAsync(), FlvDemuxerTest.Demux(flv).Packets);
                stream.Tracks!.Tracks.Should().ContainSingle().Which.Codec.Should().Be(MediaCodec.Mp3);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Publish_WithoutUsefulMetadata_Should_Expect_Both_Tracks_And_Release_Them_At_The_End(bool sendEmptyMetadata)
        {
            await using var server = await StartAsync(AcceptAll());
            var consumer = new Consumer(server);
            var (publisher, _) = await RtmpTestPublisher.PublishAsync(server.LocalEndPoint!);
            await using (publisher)
            {
                if (sendEmptyMetadata)
                {
                    await publisher.SendAsync(RtmpDataMessage.SetDataFrame(new FlvMetadata([new("width", Amf0Value.FromNumber(64))])).ToMessage(publisher.StreamId), 5);
                }

                // Audio only, but nothing said so: the packets are held for a video track that never comes.
                await publisher.SendAsync(new RtmpMessage(RtmpMessageType.Audio, publisher.StreamId, 0, (byte[])[0xAF, 0, .. FlvTestBuilder.AacConfig]), 4);
                await publisher.SendAsync(new RtmpMessage(RtmpMessageType.Audio, publisher.StreamId, 23, new byte[] { 0xAF, 1, 1, 2, 3 }), 4);
                await publisher.UnpublishAsync();

                var packets = await consumer.PacketsAsync();
                packets.Should().ContainSingle().Which.Dts.Should().Be(23);
                (await consumer.Started).Tracks!.Tracks.Should().ContainSingle().Which.Codec.Should().Be(MediaCodec.Aac);
            }
        }

        [Fact]
        public async Task PacketQueue_ByteBound_Should_Apply_Backpressure_Without_Losing_Packets()
        {
            await using var server = await StartAsync(AcceptAll(new RtmpServerOptions { PacketQueueCapacity = 2, PacketQueueMaxBytes = 16 }));
            var consumer = new Consumer(server);
            var flv = File.ReadAllBytes(FlvDemuxerTest.H264AacFixture);
            var (publisher, _) = await RtmpTestPublisher.PublishAsync(server.LocalEndPoint!);
            await using (publisher)
            {
                await publisher.SendFlvAsync(flv);
                await publisher.UnpublishAsync();

                AssertSamePackets(await consumer.PacketsAsync(), FlvDemuxerTest.Demux(flv).Packets);
            }
        }

        [Fact]
        public async Task Handshake_ThatStalls_Should_Be_Dropped_After_HandshakeTimeout()
        {
            var time = new FakeTimeProvider();
            await using var server = await StartAsync(AcceptAll(new RtmpServerOptions { TimeProvider = time }));
            await using var publisher = await RtmpTestPublisher.OpenAsync(server.LocalEndPoint!);
            await publisher.SendRawAsync([RtmpHandshake.Version, 1, 2, 3]);

            (await AdvanceUntilClosedAsync(time, publisher, TimeSpan.FromSeconds(10))).Should().BeTrue();
        }

        [Fact]
        public async Task Connection_ThatNeverPublishes_Should_Be_Dropped_After_PublishTimeout()
        {
            var time = new FakeTimeProvider();
            await using var server = await StartAsync(AcceptAll(new RtmpServerOptions { TimeProvider = time }));
            await using var publisher = await RtmpTestPublisher.OpenAsync(server.LocalEndPoint!);
            await publisher.HandshakeAsync();
            await publisher.ConnectAsync("live");

            (await AdvanceUntilClosedAsync(time, publisher, TimeSpan.FromSeconds(10))).Should().BeTrue();
        }

        [Fact]
        public async Task Authorization_ThatHangs_Should_Be_Cancelled_By_PublishTimeout()
        {
            var time = new FakeTimeProvider();
            var called = new TaskCompletionSource();
            var cancelled = new TaskCompletionSource();
            await using var server = await StartAsync(AcceptAll(new RtmpServerOptions
            {
                TimeProvider = time,
                AuthorizePublishAsync = async (_, token) =>
                {
                    called.TrySetResult();
                    await using var registration = token.Register(() => cancelled.TrySetResult());
                    await Task.Delay(Timeout.Infinite, token);
                    return true;
                }
            }));
            await using var publisher = await RtmpTestPublisher.OpenAsync(server.LocalEndPoint!);
            await publisher.HandshakeAsync();
            await publisher.ConnectAsync("live");
            var streamId = await publisher.CreateStreamAsync();
            await publisher.SendCommandAsync(RtmpCommand.Publish("streamkey"), streamId, chunkStreamId: 8);
            await called.Task.WaitAsync(TestTimeout);

            (await AdvanceUntilClosedAsync(time, publisher, TimeSpan.FromSeconds(10))).Should().BeTrue();
            await cancelled.Task.WaitAsync(TestTimeout);
        }

        [Fact]
        public async Task Publisher_ThatGoesIdle_Should_Be_Dropped_After_IdleTimeout()
        {
            var time = new FakeTimeProvider();
            await using var server = await StartAsync(AcceptAll(new RtmpServerOptions { TimeProvider = time }));
            var consumer = new Consumer(server);
            var (publisher, _) = await RtmpTestPublisher.PublishAsync(server.LocalEndPoint!);
            await using (publisher)
            {
                // The publish timeout (10 s) no longer applies once publishing: 20 s of silence is fine.
                time.Advance(TimeSpan.FromSeconds(20));
                (await publisher.WaitForCloseAsync(TimeSpan.FromMilliseconds(200))).Should().BeFalse();

                (await AdvanceUntilClosedAsync(time, publisher, TimeSpan.FromSeconds(30))).Should().BeTrue();
                await (await consumer.Started).Completion.WaitAsync(TestTimeout);
            }
        }

        [Fact]
        public async Task Consumer_ThatStopsReading_Should_Get_The_Publisher_Dropped_After_ConsumerStallTimeout()
        {
            var time = new FakeTimeProvider();
            await using var server = await StartAsync(AcceptAll(new RtmpServerOptions { TimeProvider = time, PacketQueueCapacity = 1 }));
            var consumer = new Consumer(server, read: false);
            var (publisher, _) = await RtmpTestPublisher.PublishAsync(server.LocalEndPoint!);
            await using (publisher)
            {
                await publisher.SendFlvAsync(File.ReadAllBytes(FlvDemuxerTest.H264AacFixture));

                (await AdvanceUntilClosedAsync(time, publisher, TimeSpan.FromSeconds(10))).Should().BeTrue();
                var stream = await consumer.Started;
                await stream.Completion.WaitAsync(TestTimeout);

                // Everything read from the publisher before the stall still reaches a late reader.
                var packets = new List<MediaPacket>();
                await foreach (var packet in stream.ReadPacketsAsync())
                {
                    packets.Add(packet);
                }

                packets.Should().NotBeEmpty();
            }
        }

        [Fact]
        public async Task Timeouts_OnTheSystemClock_Should_Drop_A_Stalled_Handshake()
        {
            await using var server = await StartAsync(AcceptAll());
            var options = AcceptAll();
            await using var fast = await StartAsync(new RtmpServerOptions
            {
                EndPoint = options.EndPoint,
                AuthorizePublishAsync = options.AuthorizePublishAsync,
                HandshakeTimeout = TimeSpan.FromMilliseconds(200)
            });
            await using var publisher = await RtmpTestPublisher.OpenAsync(fast.LocalEndPoint!);

            (await publisher.WaitForCloseAsync()).Should().BeTrue();
        }

        // Advances the fake clock in steps until the server closes the connection, so the test
        // doesn't depend on exactly when the server armed its timer.
        private static async Task<bool> AdvanceUntilClosedAsync(FakeTimeProvider time, RtmpTestPublisher publisher, TimeSpan step)
        {
            for (var i = 0; i < 50; i++)
            {
                time.Advance(step);
                if (await publisher.WaitForCloseAsync(TimeSpan.FromMilliseconds(100)))
                {
                    return true;
                }
            }

            return false;
        }

        internal static X509Certificate2 CreateTestCertificate()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            // A round trip through PKCS#12 gives a certificate whose private key SslStream can use on every OS.
#if NET9_0_OR_GREATER
            return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null);
#else
            return new X509Certificate2(certificate.Export(X509ContentType.Pkcs12));
#endif
        }

        // Built by hand: Amf0Writer itself refuses to nest this deep.
        private static byte[] ConnectWithNesting(int depth)
        {
            var payload = new List<byte>(Amf0Writer.Encode(Amf0Value.FromString("connect"), Amf0Value.FromNumber(1)));
            for (var i = 0; i < depth; i++)
            {
                payload.AddRange([0x03, 0x00, 0x01, (byte)'x']);
            }

            payload.AddRange(Amf0Writer.Encode(Amf0Value.FromNumber(1)));
            for (var i = 0; i < depth; i++)
            {
                payload.AddRange([0x00, 0x00, 0x09]);
            }

            var output = new System.Buffers.ArrayBufferWriter<byte>();
            var writer = new RtmpChunkWriter();
            writer.WriteSetChunkSize(output, 65536);
            writer.Write(output, RtmpCommand.ChunkStreamId, new RtmpMessage(RtmpMessageType.CommandAmf0, 0, 0, payload.ToArray()));
            return output.WrittenSpan.ToArray();
        }

        private static byte[] RandomBytes(int length, int seed)
        {
            var bytes = new byte[length];
            new Random(seed).NextBytes(bytes);
            return bytes;
        }
    }
}
