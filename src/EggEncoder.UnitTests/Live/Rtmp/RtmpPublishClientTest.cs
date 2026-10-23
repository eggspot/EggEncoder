using System.Net;
using System.Net.Sockets;
using EggEncoder.Containers.Flv;
using EggEncoder.Live.Rtmp;
using EggEncoder.Live.Rtmp.Amf0;
using EggEncoder.Media;
using EggEncoder.UnitTests.Containers.Flv;
using FluentAssertions;

namespace EggEncoder.UnitTests.Live.Rtmp
{
    public class RtmpPublishClientTest
    {
        private const string StreamKey = "secret-key-123";

        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(90);

        private static readonly RtmpPublishClientOptions Patient = new()
        {
            ConnectTimeout = TimeSpan.FromMinutes(2),
            PublishTimeout = TimeSpan.FromMinutes(2),
            WriteTimeout = TimeSpan.FromMinutes(2),
            CloseTimeout = TimeSpan.FromSeconds(30)
        };

        private static FlvDemuxerTest.Capture Fixture(string? path = null) =>
            FlvDemuxerTest.Demux(File.ReadAllBytes(path ?? FlvDemuxerTest.H264AacFixture));

        private static Uri ServerUrl(RtmpServer server, string app = "live", string scheme = "rtmp") =>
            new($"{scheme}://127.0.0.1:{server.LocalEndPoint!.Port}/{app}");

        private static async Task PublishAllAsync(RtmpPublishClient client, FlvDemuxerTest.Capture fixture)
        {
            await client.OpenAsync(fixture.TrackSets[^1]);
            foreach (var packet in fixture.Packets)
            {
                await client.WriteAsync(packet);
            }

            await client.CompleteAsync();
        }

        private static async Task<RtmpClientException> ExpectFailureAsync(Func<Task> action)
        {
            var thrown = await action.Should().ThrowAsync<RtmpClientException>();
            thrown.Which.Message.Should().NotContain(StreamKey);
            return thrown.Which;
        }

        private static MediaTrackSet H264AacTracks() => Fixture().TrackSets[^1];

        private static async Task<RtmpPublishClient> OpenScriptedAsync(RtmpScriptedServer scripted, RtmpPublishClientOptions? options = null)
        {
            var client = new RtmpPublishClient(scripted.Url(), StreamKey, options ?? Patient);
            var open = client.OpenAsync(H264AacTracks()).AsTask();
            await scripted.AcceptAsync();
            await scripted.AcceptPublishAsync();
            await open.WaitAsync(TestTimeout);
            return client;
        }

        [Fact]
        public async Task Publish_FlvFixture_ToRtmpServer_Should_Deliver_Exactly_The_Fixture_Packets()
        {
            await using var server = await RtmpServerTest.StartAsync(RtmpServerTest.AcceptAll());
            var consumer = new RtmpServerTest.Consumer(server);
            var fixture = Fixture();

            await using var client = new RtmpPublishClient(ServerUrl(server), StreamKey, Patient);
            await PublishAllAsync(client, fixture);

            var stream = await consumer.Started;
            stream.AppName.Should().Be("live");
            stream.StreamKey.Should().Be(StreamKey);
            stream.Request.PublishingType.Should().Be("live");
            stream.Request.TcUrl.Should().Be($"rtmp://127.0.0.1:{server.LocalEndPoint!.Port}/live");
            stream.Request.FlashVersion.Should().Be(RtmpCommand.DefaultFlashVersion);
            stream.ConnectProperties.Should().NotContain(p => p.Key == "fourCcList");
            RtmpServerTest.AssertSamePackets(await consumer.PacketsAsync(), fixture.Packets);
            stream.Metadata!.Width.Should().Be(64);
            stream.Tracks!.Tracks.Select(t => t.Codec).Should().Equal(MediaCodec.H264, MediaCodec.Aac);
            await stream.Completion.WaitAsync(TestTimeout);
            (await consumer.Ended).Should().BeSameAs(stream);
        }

        [Fact]
        public async Task Publish_OverRtmps_WithTrustedTestCertificate_Should_Deliver_Exactly_The_Fixture_Packets()
        {
            using var certificate = RtmpServerTest.CreateTestCertificate();
            await using var server = await RtmpServerTest.StartAsync(RtmpServerTest.AcceptAll(new RtmpServerOptions { Certificate = certificate }));
            var consumer = new RtmpServerTest.Consumer(server);
            var fixture = Fixture();
            var options = new RtmpPublishClientOptions
            {
                ConnectTimeout = Patient.ConnectTimeout,
                PublishTimeout = Patient.PublishTimeout,
                WriteTimeout = Patient.WriteTimeout,
                RemoteCertificateValidation = (_, presented, _, _) => presented?.GetCertHashString() == certificate.GetCertHashString()
            };

            await using var client = new RtmpPublishClient(ServerUrl(server, scheme: "rtmps"), StreamKey, options);
            await PublishAllAsync(client, fixture);

            RtmpServerTest.AssertSamePackets(await consumer.PacketsAsync(), fixture.Packets);
            (await consumer.Started).Request.TcUrl.Should().Be($"rtmps://127.0.0.1:{server.LocalEndPoint!.Port}/live");
        }

        [Fact]
        public async Task Publish_HevcFixture_Should_Announce_Enhanced_Rtmp_And_Deliver_The_Packets()
        {
            await using var server = await RtmpServerTest.StartAsync(RtmpServerTest.AcceptAll());
            var consumer = new RtmpServerTest.Consumer(server);
            var fixture = Fixture(FlvDemuxerTest.HevcFixture);

            await using var client = new RtmpPublishClient(ServerUrl(server), StreamKey, Patient);
            await PublishAllAsync(client, fixture);

            var stream = await consumer.Started;
            var fourCcList = stream.ConnectProperties.Single(p => p.Key == "fourCcList").Value;
            fourCcList.Type.Should().Be(Amf0Type.StrictArray);
            fourCcList.Items.Select(i => i.String).Should().Equal("hvc1");
            RtmpServerTest.AssertSamePackets(await consumer.PacketsAsync(), fixture.Packets);
        }

        [Fact]
        public async Task Publish_Mp3Fixture_Should_Deliver_The_Packets()
        {
            await using var server = await RtmpServerTest.StartAsync(RtmpServerTest.AcceptAll());
            var consumer = new RtmpServerTest.Consumer(server);
            var fixture = Fixture(FlvDemuxerTest.Mp3Fixture);

            await using var client = new RtmpPublishClient(ServerUrl(server), StreamKey, Patient);
            await PublishAllAsync(client, fixture);

            RtmpServerTest.AssertSamePackets(await consumer.PacketsAsync(), fixture.Packets);
        }

        [Fact]
        public async Task UpdateTracks_MidStream_Should_Send_The_New_Sequence_Header_Before_The_Next_Packet()
        {
            await using var server = await RtmpServerTest.StartAsync(RtmpServerTest.AcceptAll());
            var consumer = new RtmpServerTest.Consumer(server);
            var fixture = Fixture();
            var tracks = fixture.TrackSets[^1];
            var audio = tracks.Tracks.Single(t => t.Kind == MediaKind.Audio);
            var stereo = audio with { CodecConfiguration = new byte[] { 0x12, 0x10 }, ConfigVersion = audio.ConfigVersion + 1, Channels = 2 };
            var updated = new MediaTrackSet(tracks.Tracks.Select(t => t.Kind == MediaKind.Audio ? stereo : t).ToList());
            var half = fixture.Packets.Count / 2;
            var expected = fixture.Packets.Take(half)
                .Concat(fixture.Packets.Skip(half).Select(p => p.TrackId == audio.TrackId ? p with { ConfigVersion = stereo.ConfigVersion } : p))
                .ToList();

            await using var client = new RtmpPublishClient(ServerUrl(server), StreamKey, Patient);
            await client.OpenAsync(tracks);
            foreach (var packet in expected.Take(half))
            {
                await client.WriteAsync(packet);
            }

            client.UpdateTracks(updated);
            foreach (var packet in expected.Skip(half))
            {
                await client.WriteAsync(packet);
            }

            await client.CompleteAsync();

            var received = await consumer.PacketsAsync();
            received.Select(p => p.Payload.ToArray()).Should().BeEquivalentTo(expected.Select(p => p.Payload.ToArray()), o => o.WithStrictOrdering());
            received.Where(p => p.TrackId == audio.TrackId).Select(p => p.ConfigVersion).Distinct().Should().HaveCount(2);
            (await consumer.Started).Tracks!.Tracks.Single(t => t.Kind == MediaKind.Audio).CodecConfiguration.ToArray().Should().Equal(0x12, 0x10);
        }

        [Fact]
        public async Task UpdateTracks_InvalidCalls_Should_Throw()
        {
            await using var scripted = new RtmpScriptedServer();
            var tracks = H264AacTracks();
            var unopened = new RtmpPublishClient(new Uri("rtmp://127.0.0.1/live"), StreamKey);
            await using var client = await OpenScriptedAsync(scripted);
            var video = tracks.Tracks.Single(t => t.Kind == MediaKind.Video);
            var kindChanged = new MediaTrackSet([video with { Kind = MediaKind.Audio, Codec = MediaCodec.Aac }]);

            unopened.Invoking(c => c.UpdateTracks(tracks)).Should().Throw<InvalidOperationException>();
            client.Invoking(c => c.UpdateTracks(null!)).Should().Throw<ArgumentNullException>();
            client.Invoking(c => c.UpdateTracks(kindChanged)).Should().Throw<ArgumentException>();
            await unopened.DisposeAsync();
            unopened.Invoking(c => c.UpdateTracks(tracks)).Should().Throw<ObjectDisposedException>();
        }

        [Fact]
        public async Task Publish_WithCustomMetadata_Should_Send_That_Metadata()
        {
            await using var server = await RtmpServerTest.StartAsync(RtmpServerTest.AcceptAll());
            var consumer = new RtmpServerTest.Consumer(server);
            var fixture = Fixture();
            var metadata = new FlvMetadata([new("encoder", Amf0Value.FromString("custom encoder")), new("width", Amf0Value.FromNumber(64))]);

            await using var client = new RtmpPublishClient(ServerUrl(server), StreamKey, new RtmpPublishClientOptions
            {
                ConnectTimeout = Patient.ConnectTimeout,
                PublishTimeout = Patient.PublishTimeout,
                Metadata = metadata
            });
            await PublishAllAsync(client, fixture);

            await consumer.PacketsAsync();
            (await consumer.Started).Metadata!.Encoder.Should().Be("custom encoder");
        }

        [Fact]
        public async Task Publish_WithMultiSegmentApplication_Should_Connect_To_That_Application()
        {
            await using var server = await RtmpServerTest.StartAsync(RtmpServerTest.AcceptAll());
            var consumer = new RtmpServerTest.Consumer(server);

            await using var client = new RtmpPublishClient(ServerUrl(server, "app/instance/"), "key?bandwidthtest=true", Patient);
            await PublishAllAsync(client, Fixture());

            var stream = await consumer.Started;
            stream.AppName.Should().Be("app/instance");
            stream.StreamKey.Should().Be("key?bandwidthtest=true");
            client.AppName.Should().Be("app/instance");
            client.TcUrl.Should().Be($"rtmp://127.0.0.1:{server.LocalEndPoint!.Port}/app/instance");
        }

        [Fact]
        public async Task Publish_ToIpv6Loopback_Should_Deliver_The_Packets()
        {
            await using var server = await RtmpServerTest.StartAsync(RtmpServerTest.AcceptAll(new RtmpServerOptions { EndPoint = new IPEndPoint(IPAddress.IPv6Loopback, 0) }));
            var consumer = new RtmpServerTest.Consumer(server);
            var fixture = Fixture();

            await using var client = new RtmpPublishClient(new Uri($"rtmp://[::1]:{server.LocalEndPoint!.Port}/live"), StreamKey, Patient);
            await PublishAllAsync(client, fixture);

            RtmpServerTest.AssertSamePackets(await consumer.PacketsAsync(), fixture.Packets);
            (await consumer.Started).Request.TcUrl.Should().Be($"rtmp://[::1]:{server.LocalEndPoint.Port}/live");
        }

        [Fact]
        public async Task Open_WhenStreamKeyIsNotAuthorized_Should_Throw_Rejected_With_BadName()
        {
            await using var server = await RtmpServerTest.StartAsync(RtmpServerTest.AcceptAll(new RtmpServerOptions
            {
                AuthorizePublishAsync = (_, _) => ValueTask.FromResult(false)
            }));

            await using var client = new RtmpPublishClient(ServerUrl(server), StreamKey, Patient);
            var failure = await ExpectFailureAsync(() => client.OpenAsync(H264AacTracks()).AsTask());

            failure.Reason.Should().Be(RtmpClientFailureReason.Rejected);
            failure.StatusCode.Should().Be(RtmpStatusCodes.PublishBadName);
            await WaitForNoConnectionsAsync(server);
        }

        [Fact]
        public async Task Open_WhenApplicationIsRefused_Should_Throw_Rejected_With_ConnectRejected()
        {
            await using var server = await RtmpServerTest.StartAsync(RtmpServerTest.AcceptAll(new RtmpServerOptions { MaxNameLength = 3 }));

            await using var client = new RtmpPublishClient(ServerUrl(server, "toolong"), StreamKey, Patient);
            var failure = await ExpectFailureAsync(() => client.OpenAsync(H264AacTracks()).AsTask());

            failure.Reason.Should().Be(RtmpClientFailureReason.Rejected);
            failure.StatusCode.Should().Be(RtmpStatusCodes.ConnectRejected);
            await WaitForNoConnectionsAsync(server);
        }

        [Fact]
        public async Task Open_WhenNothingListens_Should_Throw_ConnectFailed()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            listener.Dispose();

            await using var client = new RtmpPublishClient(new Uri($"rtmp://127.0.0.1:{port}/live"), StreamKey, Patient);
            var failure = await ExpectFailureAsync(() => client.OpenAsync(H264AacTracks()).AsTask());

            failure.Reason.Should().Be(RtmpClientFailureReason.ConnectFailed);
            failure.InnerException.Should().BeOfType<SocketException>();
            failure.StatusCode.Should().BeNull();
        }

        [Fact]
        public async Task Open_OverRtmps_WithUntrustedCertificate_Should_Throw_TlsFailed()
        {
            using var certificate = RtmpServerTest.CreateTestCertificate();
            await using var server = await RtmpServerTest.StartAsync(RtmpServerTest.AcceptAll(new RtmpServerOptions { Certificate = certificate }));

            await using var client = new RtmpPublishClient(ServerUrl(server, scheme: "rtmps"), StreamKey, Patient);
            var failure = await ExpectFailureAsync(() => client.OpenAsync(H264AacTracks()).AsTask());

            failure.Reason.Should().Be(RtmpClientFailureReason.TlsFailed);
            await WaitForNoConnectionsAsync(server);
        }

        [Fact]
        public async Task Open_OverRtmps_ToPlainRtmpServer_Should_Throw_TlsFailed()
        {
            await using var scripted = new RtmpScriptedServer();
            await using var client = new RtmpPublishClient(new Uri(scripted.Url().ToString().Replace("rtmp://", "rtmps://")), StreamKey, Patient);
            var open = client.OpenAsync(H264AacTracks()).AsTask();
            await scripted.AcceptAsync(handshake: false);
            await scripted.SendRawAsync(new byte[64]);
            scripted.CloseConnection();

            var failure = await ExpectFailureAsync(() => open);

            failure.Reason.Should().Be(RtmpClientFailureReason.TlsFailed);
        }

        [Fact]
        public async Task Open_OverRtmps_WhenServerClosesDuringTheTlsHandshake_Should_Throw_TlsFailed()
        {
            await using var scripted = new RtmpScriptedServer();
            await using var client = new RtmpPublishClient(new Uri(scripted.Url().ToString().Replace("rtmp://", "rtmps://")), StreamKey, Patient);
            var open = client.OpenAsync(H264AacTracks()).AsTask();
            await scripted.AcceptAsync(handshake: false);
            scripted.CloseConnection();

            var failure = await ExpectFailureAsync(() => open);

            failure.Reason.Should().Be(RtmpClientFailureReason.TlsFailed);
        }

        [Fact]
        public async Task Open_WhenServerNeverAnswersTheHandshake_Should_Throw_Timeout()
        {
            await using var scripted = new RtmpScriptedServer();
            await using var client = new RtmpPublishClient(scripted.Url(), StreamKey, new RtmpPublishClientOptions { ConnectTimeout = TimeSpan.FromMilliseconds(300) });
            var open = client.OpenAsync(H264AacTracks()).AsTask();
            await scripted.AcceptAsync(handshake: false);

            var failure = await ExpectFailureAsync(() => open);

            failure.Reason.Should().Be(RtmpClientFailureReason.Timeout);
            failure.Message.Should().Contain("handshake");
            (await scripted.WaitForCloseAsync()).Should().BeTrue("a failed open frees the socket");
        }

        [Fact]
        public async Task Open_WhenServerNeverAnswersConnect_Should_Throw_Timeout()
        {
            await using var scripted = new RtmpScriptedServer();
            await using var client = new RtmpPublishClient(scripted.Url(), StreamKey, new RtmpPublishClientOptions { PublishTimeout = TimeSpan.FromMilliseconds(300) });
            var open = client.OpenAsync(H264AacTracks()).AsTask();
            await scripted.AcceptAsync();
            await scripted.ExpectCommandAsync("connect");

            var failure = await ExpectFailureAsync(() => open);

            failure.Reason.Should().Be(RtmpClientFailureReason.Timeout);
            failure.Message.Should().Contain("connect");
        }

        [Fact]
        public async Task Open_Should_Send_The_Publish_Flow_In_Ffmpeg_Order()
        {
            await using var scripted = new RtmpScriptedServer();
            await using var client = await OpenScriptedAsync(scripted);

            var commands = scripted.Received.Where(m => m.Type == RtmpMessageType.CommandAmf0).Select(m => RtmpCommand.Decode(m.Payload.Span)).ToList();
            commands.Select(c => c.Name).Should().Equal("connect", "releaseStream", "FCPublish", "createStream", "publish");
            commands[0].CommandObject.Properties.Select(p => p.Key).Should().Equal("app", "type", "flashVer", "tcUrl");
            commands[1].GetStringArgument(0).Should().Be(StreamKey);
            commands[2].GetStringArgument(0).Should().Be(StreamKey);
            commands[4].GetStringArgument(0).Should().Be(StreamKey);
            commands[4].GetStringArgument(1).Should().Be("live");
            scripted.Received[1].Type.Should().Be(RtmpMessageType.SetChunkSize);
            RtmpProtocolControl.ReadSetChunkSize(scripted.Received[1].Payload.Span).Should().Be(4096);
            scripted.Received.Single(m => m.Type == RtmpMessageType.CommandAmf0 && RtmpCommand.Decode(m.Payload.Span).Name == "publish").StreamId.Should().Be(1);
            RtmpDataMessage.Decode(scripted.Received.Single(m => m.Type == RtmpMessageType.DataAmf0).Payload.Span).TryGetMetadata()!.Width.Should().Be(64);
        }

        [Theory]
        [InlineData(MediaCodec.Av1, "av01")]
        [InlineData(MediaCodec.Vp9, "vp09")]
        [InlineData(MediaCodec.H265, "hvc1")]
        public async Task Open_EnhancedRtmpVideo_Should_Announce_Its_FourCc_In_Connect(MediaCodec codec, string fourCc)
        {
            await using var scripted = new RtmpScriptedServer();
            var tracks = new MediaTrackSet([new MediaTrackInfo(0, MediaKind.Video, codec, 1000, new byte[] { 0x81, 0x00, 0x0C, 0x00 }, 0, width: 64, height: 64)]);
            await using var client = new RtmpPublishClient(scripted.Url(), StreamKey, Patient);
            var open = client.OpenAsync(tracks).AsTask();
            await scripted.AcceptAsync();
            await scripted.AcceptPublishAsync();
            await open.WaitAsync(TestTimeout);

            var connect = RtmpCommand.Decode(scripted.Received[0].Payload.Span);
            connect.CommandObject.GetProperty("fourCcList")!.Items.Select(i => i.String).Should().Equal(fourCc);
        }

        [Theory]
        [InlineData(RtmpChunkReader.DefaultChunkSize)]
        [InlineData(65536)]
        public async Task Open_WithOutboundChunkSize_Should_Announce_That_Chunk_Size(int chunkSize)
        {
            await using var scripted = new RtmpScriptedServer();
            await using var client = await OpenScriptedAsync(scripted, new RtmpPublishClientOptions
            {
                OutboundChunkSize = chunkSize,
                ChunkReader = new RtmpChunkReaderOptions(),
                TimeProvider = TimeProvider.System
            });

            RtmpProtocolControl.ReadSetChunkSize(scripted.Received.First(m => m.Type == RtmpMessageType.SetChunkSize).Payload.Span).Should().Be(chunkSize);
        }

        [Fact]
        public async Task Open_Should_Ignore_Other_Commands_With_The_Same_Transaction_Id_And_Malformed_Statuses()
        {
            await using var scripted = new RtmpScriptedServer();
            await using var client = new RtmpPublishClient(scripted.Url(), StreamKey, Patient);
            var open = client.OpenAsync(H264AacTracks()).AsTask();
            await scripted.AcceptAsync();
            var connect = await scripted.ExpectCommandAsync("connect");
            await scripted.SendAsync(new RtmpCommand("onBWCheck", connect.TransactionId, null).ToMessage());
            await scripted.SendAsync(RtmpCommand.ConnectResult(connect.TransactionId).ToMessage());
            var createStream = await scripted.ExpectCommandAsync("createStream");
            await scripted.SendAsync(RtmpCommand.CreateStreamResult(createStream.TransactionId, 1).ToMessage());
            await scripted.ExpectCommandAsync("publish");
            await scripted.SendAsync(new RtmpCommand("onStatus", 0, null).ToMessage(1));
            await scripted.SendAsync(new RtmpCommand("onStatus", 0, null, Amf0Value.FromObject([new("level", Amf0Value.FromNumber(1))])).ToMessage(1));
            await scripted.SendStatusAsync(1, RtmpStatusCodes.LevelStatus, RtmpStatusCodes.PublishStart);

            await open.WaitAsync(TestTimeout);
        }

        [Fact]
        public async Task Open_WhenErrorCarriesNoCode_Should_Throw_Rejected_Without_A_Code()
        {
            await using var scripted = new RtmpScriptedServer();
            await using var client = new RtmpPublishClient(scripted.Url(), StreamKey, Patient);
            var open = client.OpenAsync(H264AacTracks()).AsTask();
            await scripted.AcceptAsync();
            var connect = await scripted.ExpectCommandAsync("connect");
            await scripted.SendAsync(new RtmpCommand("_error", connect.TransactionId, null).ToMessage());

            var failure = await ExpectFailureAsync(() => open);

            failure.Reason.Should().Be(RtmpClientFailureReason.Rejected);
            failure.StatusCode.Should().BeNull();
        }

        [Fact]
        public async Task Open_WhenConnectGetsError_Should_Throw_Rejected_With_The_Code()
        {
            await using var scripted = new RtmpScriptedServer();
            await using var client = new RtmpPublishClient(scripted.Url(), StreamKey, Patient);
            var open = client.OpenAsync(H264AacTracks()).AsTask();
            await scripted.AcceptAsync();
            var connect = await scripted.ExpectCommandAsync("connect");
            await scripted.SendResultAsync(connect.TransactionId + 100);
            await scripted.SendErrorAsync(connect.TransactionId, RtmpStatusCodes.ConnectRejected);

            var failure = await ExpectFailureAsync(() => open);

            failure.Reason.Should().Be(RtmpClientFailureReason.Rejected);
            failure.StatusCode.Should().Be(RtmpStatusCodes.ConnectRejected);
            failure.Message.Should().Contain("connect");
        }

        [Fact]
        public async Task Open_WhenCreateStreamGetsError_Should_Throw_Rejected()
        {
            await using var scripted = new RtmpScriptedServer();
            await using var client = new RtmpPublishClient(scripted.Url(), StreamKey, Patient);
            var open = client.OpenAsync(H264AacTracks()).AsTask();
            await scripted.AcceptAsync();
            var connect = await scripted.ExpectCommandAsync("connect");
            await scripted.SendAsync(RtmpCommand.ConnectResult(connect.TransactionId).ToMessage());
            var releaseStream = await scripted.ExpectCommandAsync("releaseStream");
            await scripted.SendErrorAsync(releaseStream.TransactionId, RtmpStatusCodes.CallFailed);
            var createStream = await scripted.ExpectCommandAsync("createStream");
            await scripted.SendAsync(new RtmpCommand("onBWDone", 0, null).ToMessage());
            await scripted.SendErrorAsync(createStream.TransactionId, RtmpStatusCodes.CallFailed);

            var failure = await ExpectFailureAsync(() => open);

            failure.Reason.Should().Be(RtmpClientFailureReason.Rejected);
            failure.StatusCode.Should().Be(RtmpStatusCodes.CallFailed);
            failure.Message.Should().Contain("createStream");
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(1.5)]
        [InlineData(5e9)]
        public async Task Open_WhenCreateStreamResultHasInvalidStreamId_Should_Throw_ProtocolError(double streamId)
        {
            await using var scripted = new RtmpScriptedServer();
            await using var client = new RtmpPublishClient(scripted.Url(), StreamKey, Patient);
            var open = client.OpenAsync(H264AacTracks()).AsTask();
            await scripted.AcceptAsync();
            var connect = await scripted.ExpectCommandAsync("connect");
            await scripted.SendAsync(RtmpCommand.ConnectResult(connect.TransactionId).ToMessage());
            var createStream = await scripted.ExpectCommandAsync("createStream");
            await scripted.SendResultAsync(createStream.TransactionId, Amf0Value.FromNumber(streamId));

            var failure = await ExpectFailureAsync(() => open);

            failure.Reason.Should().Be(RtmpClientFailureReason.ProtocolError);
            failure.InnerException.Should().BeOfType<RtmpProtocolException>();
        }

        [Fact]
        public async Task Open_WhenCreateStreamResultHasNoStreamId_Should_Throw_ProtocolError()
        {
            await using var scripted = new RtmpScriptedServer();
            await using var client = new RtmpPublishClient(scripted.Url(), StreamKey, Patient);
            var open = client.OpenAsync(H264AacTracks()).AsTask();
            await scripted.AcceptAsync();
            var connect = await scripted.ExpectCommandAsync("connect");
            await scripted.SendAsync(RtmpCommand.ConnectResult(connect.TransactionId).ToMessage());
            var createStream = await scripted.ExpectCommandAsync("createStream");
            await scripted.SendResultAsync(createStream.TransactionId, Amf0Value.FromString("1"));

            (await ExpectFailureAsync(() => open)).Reason.Should().Be(RtmpClientFailureReason.ProtocolError);
        }

        [Fact]
        public async Task Open_WhenPublishGetsErrorStatus_Should_Throw_Rejected_With_The_Code()
        {
            await using var scripted = new RtmpScriptedServer();
            await using var client = new RtmpPublishClient(scripted.Url(), StreamKey, Patient);
            var open = client.OpenAsync(H264AacTracks()).AsTask();
            await scripted.AcceptAsync();
            var connect = await scripted.ExpectCommandAsync("connect");
            await scripted.SendAsync(RtmpCommand.ConnectResult(connect.TransactionId).ToMessage());
            var createStream = await scripted.ExpectCommandAsync("createStream");
            await scripted.SendAsync(RtmpCommand.CreateStreamResult(createStream.TransactionId, 7).ToMessage());
            await scripted.ExpectCommandAsync("publish");
            await scripted.SendAsync(new RtmpCommand("onFCPublish", 0, null).ToMessage());
            await scripted.SendStatusAsync(7, RtmpStatusCodes.LevelStatus, "NetStream.Publish.Idle");
            await scripted.SendStatusAsync(7, RtmpStatusCodes.LevelError, RtmpStatusCodes.PublishBadName);

            var failure = await ExpectFailureAsync(() => open);

            failure.Reason.Should().Be(RtmpClientFailureReason.Rejected);
            failure.StatusCode.Should().Be(RtmpStatusCodes.PublishBadName);
            failure.Message.Should().Contain("publish");
        }

        [Fact]
        public async Task Open_WhenServerSendsLongStatusCode_Should_Truncate_It()
        {
            await using var scripted = new RtmpScriptedServer();
            await using var client = new RtmpPublishClient(scripted.Url(), StreamKey, Patient);
            var open = client.OpenAsync(H264AacTracks()).AsTask();
            await scripted.AcceptAsync();
            var connect = await scripted.ExpectCommandAsync("connect");
            await scripted.SendErrorAsync(connect.TransactionId, new string('x', 1000));

            var failure = await ExpectFailureAsync(() => open);

            failure.StatusCode.Should().HaveLength(128);
        }

        [Fact]
        public async Task Open_WhenServerClosesDuringConnect_Should_Throw_ConnectionLost()
        {
            await using var scripted = new RtmpScriptedServer();
            await using var client = new RtmpPublishClient(scripted.Url(), StreamKey, Patient);
            var open = client.OpenAsync(H264AacTracks()).AsTask();
            await scripted.AcceptAsync();
            await scripted.ExpectCommandAsync("connect");
            scripted.CloseConnection();

            var failure = await ExpectFailureAsync(() => open);

            failure.Reason.Should().Be(RtmpClientFailureReason.ConnectionLost);
        }

        [Fact]
        public async Task Open_WhenServerSendsMalformedChunks_Should_Throw_ProtocolError()
        {
            await using var scripted = new RtmpScriptedServer();
            await using var client = new RtmpPublishClient(scripted.Url(), StreamKey, Patient);
            var open = client.OpenAsync(H264AacTracks()).AsTask();
            await scripted.AcceptAsync();
            await scripted.ExpectCommandAsync("connect");
            await scripted.SendRawAsync([0x4A, 0, 0, 0, 0, 0, 4, 20, 1, 2, 3, 4]);

            var failure = await ExpectFailureAsync(() => open);

            failure.Reason.Should().Be(RtmpClientFailureReason.ProtocolError);
        }

        [Fact]
        public async Task Open_WhenServerSendsAmf3Command_Should_Throw_ProtocolError()
        {
            await using var scripted = new RtmpScriptedServer();
            await using var client = new RtmpPublishClient(scripted.Url(), StreamKey, Patient);
            var open = client.OpenAsync(H264AacTracks()).AsTask();
            await scripted.AcceptAsync();
            await scripted.ExpectCommandAsync("connect");
            await scripted.SendAsync(new RtmpMessage(RtmpMessageType.CommandAmf3, 0, 0, new byte[] { 0, 2, 0, 1, (byte)'x' }));

            (await ExpectFailureAsync(() => open)).Reason.Should().Be(RtmpClientFailureReason.ProtocolError);
        }

        [Fact]
        public async Task Open_Should_Answer_Pings_Peer_Bandwidth_And_Acknowledgement_Windows()
        {
            await using var scripted = new RtmpScriptedServer();
            await using var client = new RtmpPublishClient(scripted.Url(), StreamKey, Patient);
            var open = client.OpenAsync(H264AacTracks()).AsTask();
            await scripted.AcceptAsync();
            var connect = await scripted.ExpectCommandAsync("connect");
            await scripted.SendAsync(RtmpProtocolControl.CreateWindowAcknowledgementSize(16), RtmpProtocolControl.ChunkStreamId);
            await scripted.SendAsync(RtmpProtocolControl.CreateSetPeerBandwidth(5000, RtmpPeerBandwidthLimitType.Dynamic), RtmpProtocolControl.ChunkStreamId);
            await scripted.SendAsync(RtmpUserControl.PingRequest(1234).ToMessage(), RtmpProtocolControl.ChunkStreamId);
            await scripted.SendAsync(new RtmpMessage(RtmpMessageType.Audio, 0, 0, new byte[] { 0xAF }), 4);
            await scripted.SendAsync(RtmpCommand.ConnectResult(connect.TransactionId).ToMessage());

            var pong = await scripted.ExpectAsync(m => m.Type == RtmpMessageType.UserControl);
            RtmpUserControl.Read(pong.Payload.Span).Should().Be(RtmpUserControl.PingResponse(1234));
            var window = await scripted.ExpectAsync(m => m.Type == RtmpMessageType.WindowAcknowledgementSize);
            RtmpProtocolControl.ReadWindowAcknowledgementSize(window.Payload.Span).Should().Be(5000);
            var acknowledgement = await scripted.ExpectAsync(m => m.Type == RtmpMessageType.Acknowledgement);
            RtmpProtocolControl.ReadAcknowledgement(acknowledgement.Payload.Span).Should().BeGreaterThanOrEqualTo(16);
            var createStream = await scripted.ExpectCommandAsync("createStream");
            await scripted.SendAsync(RtmpCommand.CreateStreamResult(createStream.TransactionId, 1).ToMessage());
            await scripted.ExpectCommandAsync("publish");
            await scripted.SendStatusAsync(1, RtmpStatusCodes.LevelStatus, RtmpStatusCodes.PublishStart);
            await open.WaitAsync(TestTimeout);
        }

        [Fact]
        public async Task Publishing_Should_Answer_Pings_From_The_Server()
        {
            await using var scripted = new RtmpScriptedServer();
            await using var client = await OpenScriptedAsync(scripted);

            await scripted.SendAsync(RtmpUserControl.PingRequest(99).ToMessage(), RtmpProtocolControl.ChunkStreamId);

            var pong = await scripted.ExpectAsync(m => m.Type == RtmpMessageType.UserControl);
            RtmpUserControl.Read(pong.Payload.Span).Should().Be(RtmpUserControl.PingResponse(99));
        }

        [Fact]
        public async Task Write_AfterServerSendsErrorStatus_Should_Throw_Rejected()
        {
            await using var scripted = new RtmpScriptedServer();
            await using var client = await OpenScriptedAsync(scripted);
            var fixture = Fixture();

            await scripted.SendStatusAsync(1, RtmpStatusCodes.LevelStatus, "NetStream.Publish.Something");
            await scripted.SendAsync(new RtmpCommand("onCustom", 0, null).ToMessage());
            await scripted.SendStatusAsync(1, RtmpStatusCodes.LevelError, "NetStream.Publish.Failed");
            var failure = await ExpectFailureAsync(() => WriteUntilFailureAsync(client, fixture.Packets[0]));

            failure.Reason.Should().Be(RtmpClientFailureReason.Rejected);
            failure.StatusCode.Should().Be("NetStream.Publish.Failed");
            (await scripted.WaitForCloseAsync()).Should().BeTrue();
        }

        [Fact]
        public async Task Write_AfterServerSendsMalformedChunks_Should_Throw_ProtocolError()
        {
            await using var scripted = new RtmpScriptedServer();
            await using var client = await OpenScriptedAsync(scripted);

            await scripted.SendRawAsync([0x4A, 0, 0, 0, 0, 0, 4, 20, 1, 2, 3, 4]);
            var failure = await ExpectFailureAsync(() => WriteUntilFailureAsync(client, Fixture().Packets[0]));

            failure.Reason.Should().Be(RtmpClientFailureReason.ProtocolError);
        }

        [Fact]
        public async Task Write_WhenServerDisconnectsThePublisher_Should_Throw_ConnectionLost_And_Keep_Throwing()
        {
            await using var server = await RtmpServerTest.StartAsync(RtmpServerTest.AcceptAll());
            var consumer = new RtmpServerTest.Consumer(server);
            var fixture = Fixture();
            await using var client = new RtmpPublishClient(ServerUrl(server), StreamKey, Patient);
            await client.OpenAsync(fixture.TrackSets[^1]);
            await client.WriteAsync(fixture.Packets[0]);

            (await consumer.Started).Disconnect();
            var failure = await ExpectFailureAsync(() => WriteUntilFailureAsync(client, fixture.Packets[1]));

            failure.Reason.Should().Be(RtmpClientFailureReason.ConnectionLost);
            (await client.Invoking(c => c.WriteAsync(fixture.Packets[1]).AsTask()).Should().ThrowAsync<RtmpClientException>()).Which.Should().BeSameAs(failure);
            (await client.Invoking(c => c.CompleteAsync().AsTask()).Should().ThrowAsync<RtmpClientException>()).Which.Should().BeSameAs(failure);
            client.Invoking(c => c.UpdateTracks(fixture.TrackSets[^1])).Should().Throw<RtmpClientException>().Which.Should().BeSameAs(failure);
            await WaitForNoConnectionsAsync(server);
        }

        [Fact]
        public async Task Write_WhenServerStopsReading_Should_Throw_Timeout()
        {
            await using var scripted = new RtmpScriptedServer();
            await using var client = await OpenScriptedAsync(scripted, new RtmpPublishClientOptions { WriteTimeout = TimeSpan.FromMilliseconds(300) });
            var tracks = H264AacTracks();
            var big = new MediaPacket(tracks.Tracks[0].TrackId, 0, 0, true, tracks.Tracks[0].ConfigVersion, new byte[4 * 1024 * 1024]);

            var failure = await ExpectFailureAsync(async () =>
            {
                for (var i = 0; i < 64; i++)
                {
                    await client.WriteAsync(big with { Pts = i, Dts = i });
                }
            });

            failure.Reason.Should().Be(RtmpClientFailureReason.Timeout);
        }

        [Fact]
        public async Task Write_InvalidPacket_Should_Throw_ArgumentException_And_Keep_Publishing()
        {
            await using var server = await RtmpServerTest.StartAsync(RtmpServerTest.AcceptAll());
            var consumer = new RtmpServerTest.Consumer(server);
            var fixture = Fixture();
            await using var client = new RtmpPublishClient(ServerUrl(server), StreamKey, Patient);
            await client.OpenAsync(fixture.TrackSets[^1]);

            await client.Invoking(c => c.WriteAsync(fixture.Packets[0] with { TrackId = 99 }).AsTask()).Should().ThrowAsync<ArgumentException>();
            await client.Invoking(c => c.WriteAsync(fixture.Packets[0] with { Dts = -5, Pts = -5 }).AsTask()).Should().ThrowAsync<ArgumentOutOfRangeException>();
            foreach (var packet in fixture.Packets)
            {
                await client.WriteAsync(packet);
            }

            await client.CompleteAsync();
            RtmpServerTest.AssertSamePackets(await consumer.PacketsAsync(), fixture.Packets);
        }

        [Fact]
        public async Task Write_PacketLargerThanAnRtmpMessage_Should_Throw_ArgumentException_And_Keep_Publishing()
        {
            await using var server = await RtmpServerTest.StartAsync(RtmpServerTest.AcceptAll());
            var consumer = new RtmpServerTest.Consumer(server);
            var fixture = Fixture();
            await using var client = new RtmpPublishClient(ServerUrl(server), StreamKey, Patient);
            await client.OpenAsync(fixture.TrackSets[^1]);
            var huge = fixture.Packets[0] with { Payload = new byte[RtmpMessage.MaxPayloadLength] };

            await client.Invoking(c => c.WriteAsync(huge).AsTask()).Should().ThrowAsync<ArgumentException>().WithParameterName("packet");
            foreach (var packet in fixture.Packets)
            {
                await client.WriteAsync(packet);
            }

            await client.CompleteAsync();
            RtmpServerTest.AssertSamePackets(await consumer.PacketsAsync(), fixture.Packets);
        }

        [Fact]
        public async Task Write_WithCancelledToken_Should_Throw_And_Keep_Publishing()
        {
            await using var server = await RtmpServerTest.StartAsync(RtmpServerTest.AcceptAll());
            var consumer = new RtmpServerTest.Consumer(server);
            var fixture = Fixture();
            await using var client = new RtmpPublishClient(ServerUrl(server), StreamKey, Patient);
            await client.OpenAsync(fixture.TrackSets[^1]);

            await client.Invoking(c => c.WriteAsync(fixture.Packets[0], new CancellationToken(canceled: true)).AsTask()).Should().ThrowAsync<OperationCanceledException>();
            foreach (var packet in fixture.Packets)
            {
                await client.WriteAsync(packet);
            }

            await client.CompleteAsync();
            RtmpServerTest.AssertSamePackets(await consumer.PacketsAsync(), fixture.Packets);
        }

        [Fact]
        public async Task Complete_WhenServerKeepsTheConnectionOpen_Should_Close_After_The_Close_Timeout()
        {
            await using var scripted = new RtmpScriptedServer();
            await using var client = await OpenScriptedAsync(scripted, new RtmpPublishClientOptions { CloseTimeout = TimeSpan.FromMilliseconds(200) });

            await client.CompleteAsync().AsTask().WaitAsync(TestTimeout);

            var unpublish = await scripted.ExpectCommandAsync("FCUnpublish");
            unpublish.GetStringArgument(0).Should().Be(StreamKey);
            (await scripted.ExpectCommandAsync("deleteStream")).GetNumberArgument(0).Should().Be(1);
            (await scripted.WaitForCloseAsync()).Should().BeTrue();
            await client.Invoking(c => c.WriteAsync(Fixture().Packets[0]).AsTask()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*already completed*");
            await client.Invoking(c => c.CompleteAsync().AsTask()).Should().ThrowAsync<InvalidOperationException>();
        }

        [Fact]
        public async Task Complete_WhenServerResetsTheConnection_Should_Still_Complete()
        {
            await using var scripted = new RtmpScriptedServer();
            await using var client = await OpenScriptedAsync(scripted);

            var complete = client.CompleteAsync().AsTask();
            await scripted.ExpectCommandAsync("deleteStream");
            scripted.ResetConnection();

            await complete.WaitAsync(TestTimeout);
            await client.Invoking(c => c.WriteAsync(Fixture().Packets[0]).AsTask()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*already completed*");
        }

        [Fact]
        public async Task Write_CancelledWhileTheServerIsNotReading_Should_Throw_And_Leave_The_Client_Failed()
        {
            await using var scripted = new RtmpScriptedServer();
            await using var client = await OpenScriptedAsync(scripted);
            var tracks = H264AacTracks();
            var big = new MediaPacket(tracks.Tracks[0].TrackId, 0, 0, true, tracks.Tracks[0].ConfigVersion, new byte[4 * 1024 * 1024]);
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

            await FluentActions.Awaiting(async () =>
            {
                for (var i = 0; i < 64; i++)
                {
                    await client.WriteAsync(big with { Pts = i, Dts = i }, cancel.Token);
                }
            }).Should().ThrowAsync<OperationCanceledException>();

            var failure = await ExpectFailureAsync(() => client.WriteAsync(big).AsTask());
            failure.Reason.Should().Be(RtmpClientFailureReason.ConnectionLost);
            failure.Message.Should().Contain("cancelled");
        }

        [Fact]
        public async Task Complete_WithCancelledToken_Should_Throw_OperationCanceled()
        {
            await using var scripted = new RtmpScriptedServer();
            await using var client = await OpenScriptedAsync(scripted);

            await client.Invoking(c => c.CompleteAsync(new CancellationToken(canceled: true)).AsTask()).Should().ThrowAsync<OperationCanceledException>();
        }

        [Fact]
        public async Task Complete_CancelledWhileWaitingForTheServerToClose_Should_Close_And_Throw()
        {
            await using var scripted = new RtmpScriptedServer();
            await using var client = await OpenScriptedAsync(scripted);
            using var cancel = new CancellationTokenSource();

            var complete = client.CompleteAsync(cancel.Token).AsTask();
            await scripted.ExpectCommandAsync("deleteStream");
            cancel.Cancel();

            await complete.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
            (await scripted.WaitForCloseAsync()).Should().BeTrue();
        }

        [Fact]
        public async Task Open_CancelledByCaller_Should_Throw_OperationCanceled_And_Close()
        {
            await using var scripted = new RtmpScriptedServer();
            await using var client = new RtmpPublishClient(scripted.Url(), StreamKey, Patient);
            using var cancel = new CancellationTokenSource();
            var open = client.OpenAsync(H264AacTracks(), cancel.Token).AsTask();
            await scripted.AcceptAsync();
            await scripted.ExpectCommandAsync("connect");

            cancel.Cancel();

            await open.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
            (await scripted.WaitForCloseAsync()).Should().BeTrue();
            await client.Invoking(c => c.WriteAsync(Fixture().Packets[0]).AsTask()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*new client*");
        }

        [Fact]
        public async Task Open_DisposedWhileOpening_Should_Throw_OperationCanceled()
        {
            await using var scripted = new RtmpScriptedServer();
            var client = new RtmpPublishClient(scripted.Url(), StreamKey, Patient);
            var open = client.OpenAsync(H264AacTracks()).AsTask();
            await scripted.AcceptAsync();
            await scripted.ExpectCommandAsync("connect");

            await client.DisposeAsync();

            await open.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        }

        [Fact]
        public async Task Open_UnsupportedTracks_Should_Throw_Before_Connecting()
        {
            await using var client = new RtmpPublishClient(new Uri("rtmp://127.0.0.1:1/live"), StreamKey);
            var opus = new MediaTrackSet([new MediaTrackInfo(0, MediaKind.Audio, MediaCodec.Opus, 48000, new byte[19], 0, 48000, 2)]);
            var noConfig = new MediaTrackSet([new MediaTrackInfo(0, MediaKind.Video, MediaCodec.H264, 1000, ReadOnlyMemory<byte>.Empty, 0)]);

            await client.Invoking(c => c.OpenAsync(opus).AsTask()).Should().ThrowAsync<NotSupportedException>();
            await client.Invoking(c => c.OpenAsync(noConfig).AsTask()).Should().ThrowAsync<ArgumentException>();
            await client.Invoking(c => c.OpenAsync(null!).AsTask()).Should().ThrowAsync<ArgumentNullException>();
        }

        [Fact]
        public async Task Open_Twice_Should_Throw_InvalidOperation()
        {
            await using var scripted = new RtmpScriptedServer();
            await using var client = await OpenScriptedAsync(scripted);

            await client.Invoking(c => c.OpenAsync(H264AacTracks()).AsTask()).Should().ThrowAsync<InvalidOperationException>();
        }

        [Fact]
        public async Task Write_And_Complete_BeforeOpen_Should_Throw_InvalidOperation()
        {
            await using var client = new RtmpPublishClient(new Uri("rtmp://127.0.0.1/live"), StreamKey);

            await client.Invoking(c => c.WriteAsync(Fixture().Packets[0]).AsTask()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*OpenAsync*");
            await client.Invoking(c => c.CompleteAsync().AsTask()).Should().ThrowAsync<InvalidOperationException>();
        }

        [Fact]
        public async Task Calls_AfterDispose_Should_Throw_ObjectDisposed_And_Dispose_Twice_Should_Do_Nothing()
        {
            var client = new RtmpPublishClient(new Uri("rtmp://127.0.0.1/live"), StreamKey);
            await client.DisposeAsync();
            await client.DisposeAsync();

            await client.Invoking(c => c.OpenAsync(H264AacTracks()).AsTask()).Should().ThrowAsync<ObjectDisposedException>();
            await client.Invoking(c => c.WriteAsync(Fixture().Packets[0]).AsTask()).Should().ThrowAsync<ObjectDisposedException>();
            await client.Invoking(c => c.CompleteAsync().AsTask()).Should().ThrowAsync<ObjectDisposedException>();
        }

        [Fact]
        public async Task Dispose_WhilePublishing_Should_Close_The_Connection()
        {
            await using var scripted = new RtmpScriptedServer();
            var client = await OpenScriptedAsync(scripted);

            await client.DisposeAsync();

            (await scripted.WaitForCloseAsync()).Should().BeTrue();
            scripted.Received.Where(m => m.Type == RtmpMessageType.CommandAmf0).Select(m => RtmpCommand.Decode(m.Payload.Span).Name).Should().NotContain("FCUnpublish");
        }

        [Theory]
        [InlineData("rtmp://host/live", "live", "rtmp://host/live")]
        [InlineData("rtmp://host:1935/live/", "live", "rtmp://host:1935/live")]
        [InlineData("rtmps://live-api-s.facebook.com:443/rtmp/", "rtmp", "rtmps://live-api-s.facebook.com:443/rtmp")]
        [InlineData("RTMP://Host/app/instance", "app/instance", "rtmp://host/app/instance")]
        [InlineData("rtmp://[::1]:5000/live", "live", "rtmp://[::1]:5000/live")]
        [InlineData("rtmp://host/a%20b", "a b", "rtmp://host/a b")]
        [InlineData("rtmps://host/rtmp", "rtmp", "rtmps://host/rtmp")]
        public void Constructor_ServerUrl_Should_Derive_Application_And_TcUrl(string url, string app, string tcUrl)
        {
            var client = new RtmpPublishClient(new Uri(url), StreamKey);

            client.AppName.Should().Be(app);
            client.TcUrl.Should().Be(tcUrl);
        }

        [Theory]
        [InlineData("http://host/live")]
        [InlineData("rtmp://host")]
        [InlineData("rtmp://host/")]
        [InlineData("rtmp://user:pass@host/live")]
        [InlineData("rtmp://host/live?token=1")]
        [InlineData("rtmp://host/live#x")]
        [InlineData("/live")]
        public void Constructor_InvalidServerUrl_Should_Throw_ArgumentException(string url)
        {
            var act = () => new RtmpPublishClient(new Uri(url, UriKind.RelativeOrAbsolute), StreamKey);

            act.Should().Throw<ArgumentException>().WithParameterName("serverUrl").Which.Message.Should().NotContain("pass@");
        }

        [Fact]
        public void Constructor_InvalidArguments_Should_Throw()
        {
            var url = new Uri("rtmp://host/live");

            FluentActions.Invoking(() => new RtmpPublishClient(null!, StreamKey)).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => new RtmpPublishClient(url, null!)).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => new RtmpPublishClient(url, string.Empty)).Should().Throw<ArgumentException>().WithParameterName("streamKey");
        }

        [Fact]
        public void Options_Should_Have_Secure_Defaults()
        {
            var options = new RtmpPublishClientOptions();

            options.ConnectTimeout.Should().Be(TimeSpan.FromSeconds(10));
            options.PublishTimeout.Should().Be(TimeSpan.FromSeconds(10));
            options.WriteTimeout.Should().Be(TimeSpan.FromSeconds(30));
            options.CloseTimeout.Should().Be(TimeSpan.FromSeconds(2));
            options.OutboundChunkSize.Should().Be(4096);
            options.RemoteCertificateValidation.Should().BeNull();
            options.Metadata.Should().BeNull();
            options.TimeProvider.Should().BeSameAs(TimeProvider.System);
            options.ChunkReader.MaxMessageLength.Should().Be(new RtmpChunkReaderOptions().MaxMessageLength);
            RtmpPublishClient.DefaultRtmpPort.Should().Be(1935);
            RtmpPublishClient.DefaultRtmpsPort.Should().Be(443);
        }

        public static TheoryData<Func<RtmpPublishClientOptions>> InvalidOptions => new()
        {
            () => new RtmpPublishClientOptions { ConnectTimeout = TimeSpan.Zero },
            () => new RtmpPublishClientOptions { PublishTimeout = TimeSpan.FromHours(2) },
            () => new RtmpPublishClientOptions { WriteTimeout = TimeSpan.FromSeconds(-1) },
            () => new RtmpPublishClientOptions { CloseTimeout = TimeSpan.Zero },
            () => new RtmpPublishClientOptions { OutboundChunkSize = 127 },
            () => new RtmpPublishClientOptions { OutboundChunkSize = RtmpChunkReaderOptions.HardMaxInboundChunkSize + 1 }
        };

        [Theory]
        [MemberData(nameof(InvalidOptions))]
        public void Options_OutOfRange_Should_Throw(Func<RtmpPublishClientOptions> create)
        {
            create.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Options_NullValues_Should_Throw()
        {
            FluentActions.Invoking(() => new RtmpPublishClientOptions { ChunkReader = null! }).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => new RtmpPublishClientOptions { TimeProvider = null! }).Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Exception_Should_Carry_Reason_Code_And_Inner_Exception()
        {
            var inner = new IOException("inner");
            var exception = new RtmpClientException(RtmpClientFailureReason.Rejected, "message", RtmpStatusCodes.PublishBadName, inner);

            exception.Reason.Should().Be(RtmpClientFailureReason.Rejected);
            exception.StatusCode.Should().Be(RtmpStatusCodes.PublishBadName);
            exception.InnerException.Should().BeSameAs(inner);
            exception.Should().BeAssignableTo<IOException>();
            new RtmpClientException(RtmpClientFailureReason.Timeout, "m").StatusCode.Should().BeNull();
        }

        private static async Task WriteUntilFailureAsync(RtmpPublishClient client, MediaPacket packet)
        {
            using var timeout = new CancellationTokenSource(TestTimeout);
            for (var i = 0; !timeout.IsCancellationRequested; i++)
            {
                await client.WriteAsync(packet with { Pts = packet.Pts + i, Dts = packet.Dts + i });
                await Task.Delay(10);
            }
        }

        private static async Task WaitForNoConnectionsAsync(RtmpServer server)
        {
            using var timeout = new CancellationTokenSource(TestTimeout);
            while (server.ConnectionCount > 0)
            {
                await Task.Delay(10, timeout.Token);
            }
        }
    }
}
