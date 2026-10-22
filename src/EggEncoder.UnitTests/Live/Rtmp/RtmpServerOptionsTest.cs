using System.Net;
using EggEncoder.Live.Rtmp;
using EggEncoder.Live.Rtmp.Amf0;
using FluentAssertions;

namespace EggEncoder.UnitTests.Live.Rtmp
{
    public class RtmpServerOptionsTest
    {
        [Fact]
        public void Defaults_Should_Match_The_Security_Posture()
        {
            var options = new RtmpServerOptions();

            options.EndPoint.Should().Be(new IPEndPoint(IPAddress.Loopback, 1935));
            options.Certificate.Should().BeNull();
            options.AuthorizePublishAsync.Should().BeNull();
            options.TimeProvider.Should().BeSameAs(TimeProvider.System);
            options.ChunkReader.MaxInboundChunkSize.Should().Be(64 * 1024);
            options.MaxConnections.Should().Be(64);
            options.MaxNameLength.Should().Be(256);
            options.OutboundChunkSize.Should().Be(4096);
            options.AcknowledgementWindowSize.Should().Be(2_500_000u);
            options.PacketQueueCapacity.Should().Be(1024);
            options.PacketQueueMaxBytes.Should().Be(64 * 1024 * 1024);
            options.HandshakeTimeout.Should().Be(TimeSpan.FromSeconds(10));
            options.PublishTimeout.Should().Be(TimeSpan.FromSeconds(10));
            options.IdleTimeout.Should().Be(TimeSpan.FromSeconds(30));
            options.ConsumerStallTimeout.Should().Be(TimeSpan.FromSeconds(10));
            new RtmpServer().Options.EndPoint.Port.Should().Be(RtmpServerOptions.DefaultPort);
        }

        [Fact]
        public void Bounds_Should_Accept_Their_Limits()
        {
            var options = new RtmpServerOptions
            {
                MaxConnections = RtmpServerOptions.HardMaxConnections,
                MaxNameLength = 1,
                OutboundChunkSize = RtmpChunkReaderOptions.HardMaxInboundChunkSize,
                AcknowledgementWindowSize = uint.MaxValue,
                PacketQueueCapacity = RtmpServerOptions.HardMaxPacketQueueCapacity,
                PacketQueueMaxBytes = RtmpServerOptions.HardMaxPacketQueueBytes,
                HandshakeTimeout = RtmpServerOptions.HardMaxTimeout,
                PublishTimeout = TimeSpan.FromTicks(1),
                IdleTimeout = TimeSpan.FromMinutes(5),
                ConsumerStallTimeout = TimeSpan.FromSeconds(1)
            };

            options.MaxConnections.Should().Be(10_000);
            options.OutboundChunkSize.Should().Be(0xFFFFFF);
            options.HandshakeTimeout.Should().Be(TimeSpan.FromHours(1));
        }

        public static TheoryData<string, Action> OutOfRange => new()
        {
            { nameof(RtmpServerOptions.MaxConnections), () => _ = new RtmpServerOptions { MaxConnections = 0 } },
            { nameof(RtmpServerOptions.MaxConnections), () => _ = new RtmpServerOptions { MaxConnections = RtmpServerOptions.HardMaxConnections + 1 } },
            { nameof(RtmpServerOptions.MaxNameLength), () => _ = new RtmpServerOptions { MaxNameLength = 0 } },
            { nameof(RtmpServerOptions.MaxNameLength), () => _ = new RtmpServerOptions { MaxNameLength = RtmpServerOptions.HardMaxNameLength + 1 } },
            { nameof(RtmpServerOptions.OutboundChunkSize), () => _ = new RtmpServerOptions { OutboundChunkSize = 127 } },
            { nameof(RtmpServerOptions.OutboundChunkSize), () => _ = new RtmpServerOptions { OutboundChunkSize = 0x1000000 } },
            { nameof(RtmpServerOptions.AcknowledgementWindowSize), () => _ = new RtmpServerOptions { AcknowledgementWindowSize = 0 } },
            { nameof(RtmpServerOptions.PacketQueueCapacity), () => _ = new RtmpServerOptions { PacketQueueCapacity = 0 } },
            { nameof(RtmpServerOptions.PacketQueueMaxBytes), () => _ = new RtmpServerOptions { PacketQueueMaxBytes = RtmpServerOptions.HardMaxPacketQueueBytes + 1 } },
            { nameof(RtmpServerOptions.HandshakeTimeout), () => _ = new RtmpServerOptions { HandshakeTimeout = TimeSpan.Zero } },
            { nameof(RtmpServerOptions.PublishTimeout), () => _ = new RtmpServerOptions { PublishTimeout = Timeout.InfiniteTimeSpan } },
            { nameof(RtmpServerOptions.IdleTimeout), () => _ = new RtmpServerOptions { IdleTimeout = TimeSpan.FromHours(2) } },
            { nameof(RtmpServerOptions.ConsumerStallTimeout), () => _ = new RtmpServerOptions { ConsumerStallTimeout = TimeSpan.FromSeconds(-1) } }
        };

        [Theory]
        [MemberData(nameof(OutOfRange))]
        public void Bounds_OutOfRange_Should_Throw_Naming_The_Option(string name, Action create)
        {
            create.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be(name);
        }

        [Fact]
        public void References_SetToNull_Should_Throw()
        {
            FluentActions.Invoking(() => new RtmpServerOptions { EndPoint = null! }).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => new RtmpServerOptions { ChunkReader = null! }).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => new RtmpServerOptions { TimeProvider = null! }).Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void PublishRequest_Should_Expose_Connect_Properties_And_Keep_The_Key_Out_Of_ToString()
        {
            var request = new RtmpPublishRequest("live", "s3cret", "live", new IPEndPoint(IPAddress.Loopback, 5000),
            [
                new("app", Amf0Value.FromString("live")),
                new("flashVer", Amf0Value.FromLongString("FMLE/3.0 (compatible; obs-studio/30.0)")),
                new("tcUrl", Amf0Value.FromNumber(1))
            ]);

            request.FlashVersion.Should().Be("FMLE/3.0 (compatible; obs-studio/30.0)");
            request.TcUrl.Should().BeNull("a tcUrl that isn't a string reads as absent");
            request.ToString().Should().Be("publish of app 'live' from 127.0.0.1:5000").And.NotContain("s3cret");
            new RtmpPublishRequest("live", "k", string.Empty, null, []).ToString().Should().Contain("an unknown address");
            new RtmpPublishRequest("live", "k", string.Empty, null, []).FlashVersion.Should().BeNull();
        }
    }
}
