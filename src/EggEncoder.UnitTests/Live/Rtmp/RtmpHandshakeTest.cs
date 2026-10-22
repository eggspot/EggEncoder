using EggEncoder.Live.Rtmp;
using FluentAssertions;

namespace EggEncoder.UnitTests.Live.Rtmp
{
    public class RtmpHandshakeTest
    {
        private const int PacketLength = RtmpHandshake.PacketLength;

        [Fact]
        public void Handshake_ClientAndServer_Should_Complete_And_Echo_Each_Others_Packets()
        {
            var client = RtmpHandshake.CreateClient();
            var server = RtmpHandshake.CreateServer();

            server.BytesExpected.Should().Be(0);
            server.Start().Should().BeEmpty();
            server.BytesExpected.Should().Be(1 + PacketLength);
            var c0c1 = client.Start();
            client.BytesExpected.Should().Be(1 + (2 * PacketLength));
            c0c1.Should().HaveCount(1 + PacketLength);
            c0c1[0].Should().Be(RtmpHandshake.Version);
            c0c1.AsSpan(1, 8).ToArray().Should().OnlyContain(b => b == 0, "time 0 and the four zero bytes");

            var s0s1s2 = server.Process(c0c1);
            s0s1s2.Should().HaveCount(1 + (2 * PacketLength));
            s0s1s2[0].Should().Be(RtmpHandshake.Version);
            s0s1s2.AsSpan(1 + PacketLength).ToArray().Should().Equal(c0c1.AsSpan(1).ToArray(), "S2 echoes C1");
            server.IsComplete.Should().BeFalse();
            server.BytesExpected.Should().Be(PacketLength);

            var c2 = client.Process(s0s1s2);
            c2.Should().Equal(s0s1s2.AsSpan(1, PacketLength).ToArray(), "C2 echoes S1");
            client.IsComplete.Should().BeTrue();
            client.BytesExpected.Should().Be(0);

            server.Process(c2).Should().BeEmpty();
            server.IsComplete.Should().BeTrue();
            server.BytesExpected.Should().Be(0);
        }

        [Fact]
        public void Handshake_RandomBytes_Should_Differ_Between_Handshakes()
        {
            var first = RtmpHandshake.CreateClient().Start();
            var second = RtmpHandshake.CreateClient().Start();

            first.AsSpan(9).ToArray().Should().NotEqual(second.AsSpan(9).ToArray());
        }

        [Fact]
        public void Server_EchoThatDoesNotMatch_Should_Still_Complete()
        {
            // Digest-handshake publishers don't echo S1 byte for byte; nothing depends on it.
            var server = RtmpHandshake.CreateServer();
            server.Start();
            server.Process(RtmpHandshake.CreateClient().Start());

            server.Process(new byte[PacketLength]).Should().BeEmpty();
            server.IsComplete.Should().BeTrue();
        }

        [Fact]
        public void Start_Twice_Should_Throw()
        {
            var handshake = RtmpHandshake.CreateClient();
            handshake.Start();

            handshake.Invoking(h => h.Start()).Should().Throw<InvalidOperationException>();
        }

        [Fact]
        public void Process_BeforeStart_Should_Throw()
        {
            RtmpHandshake.CreateServer().Invoking(h => h.Process(new byte[1 + PacketLength]))
                .Should().Throw<InvalidOperationException>().WithMessage("*Start*");
        }

        [Fact]
        public void Process_AfterComplete_Should_Throw()
        {
            var client = RtmpHandshake.CreateClient();
            var server = RtmpHandshake.CreateServer();
            server.Start();
            client.Process(server.Process(client.Start()));

            client.Invoking(h => h.Process(new byte[1])).Should().Throw<InvalidOperationException>().WithMessage("*complete*");
        }

        [Theory]
        [InlineData(0)]
        [InlineData(PacketLength)]
        [InlineData(PacketLength + 2)]
        public void Process_WrongLength_Should_Throw(int length)
        {
            var server = RtmpHandshake.CreateServer();
            server.Start();

            server.Invoking(h => h.Process(new byte[length])).Should().Throw<ArgumentException>().WithParameterName("peerBytes");
        }

        [Theory]
        [InlineData(6, true)]
        [InlineData(8, true)]
        [InlineData(0, false)]
        [InlineData(4, false)]
        [InlineData(0x47, false)] // 'G', an HTTP request
        public void Server_VersionOtherThan3_Should_Throw_Protocol_Exception(byte version, bool isRtmpe)
        {
            var server = RtmpHandshake.CreateServer();
            server.Start();
            var c0c1 = new byte[1 + PacketLength];
            c0c1[0] = version;

            var thrown = server.Invoking(h => h.Process(c0c1)).Should().Throw<RtmpProtocolException>().Which;
            thrown.Message.Should().Contain(isRtmpe ? "RTMPE" : "unsupported version");
        }

        [Fact]
        public void Client_ServerVersionOtherThan3_Should_Throw_Protocol_Exception()
        {
            var client = RtmpHandshake.CreateClient();
            client.Start();
            var s0s1s2 = new byte[1 + (2 * PacketLength)];
            s0s1s2[0] = 6;

            client.Invoking(h => h.Process(s0s1s2)).Should().Throw<RtmpProtocolException>().WithMessage("*RTMPE*");
            client.IsComplete.Should().BeFalse();
        }
    }
}
