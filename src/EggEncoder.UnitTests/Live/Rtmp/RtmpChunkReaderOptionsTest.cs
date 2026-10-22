using EggEncoder.Live.Rtmp;
using FluentAssertions;

namespace EggEncoder.UnitTests.Live.Rtmp
{
    public class RtmpChunkReaderOptionsTest
    {
        [Fact]
        public void Defaults_Should_Match_The_Security_Posture()
        {
            var options = new RtmpChunkReaderOptions();

            options.MaxInboundChunkSize.Should().Be(65536);
            options.MaxMessageLength.Should().Be(8 * 1024 * 1024);
            options.MaxChunkStreams.Should().Be(64);
            options.MaxBufferedBytes.Should().Be(16 * 1024 * 1024);
        }

        [Fact]
        public void Limits_AtTheirHardMaximum_Should_Be_Accepted()
        {
            var options = new RtmpChunkReaderOptions
            {
                MaxInboundChunkSize = RtmpChunkReaderOptions.HardMaxInboundChunkSize,
                MaxMessageLength = RtmpChunkReaderOptions.HardMaxMessageLength,
                MaxChunkStreams = RtmpChunkReaderOptions.HardMaxChunkStreams,
                MaxBufferedBytes = RtmpChunkReaderOptions.HardMaxBufferedBytes
            };

            options.MaxInboundChunkSize.Should().Be(RtmpMessage.MaxPayloadLength);
            options.MaxMessageLength.Should().Be(RtmpMessage.MaxPayloadLength);
            options.MaxChunkStreams.Should().Be(1024);
            options.MaxBufferedBytes.Should().Be(256 * 1024 * 1024);
        }

        [Fact]
        public void Limits_OutsideOneToTheHardMaximum_Should_Throw()
        {
            FluentActions.Invoking(() => new RtmpChunkReaderOptions { MaxInboundChunkSize = 0 })
                .Should().Throw<ArgumentOutOfRangeException>().WithParameterName(nameof(RtmpChunkReaderOptions.MaxInboundChunkSize));
            FluentActions.Invoking(() => new RtmpChunkReaderOptions { MaxInboundChunkSize = RtmpChunkReaderOptions.HardMaxInboundChunkSize + 1 })
                .Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => new RtmpChunkReaderOptions { MaxMessageLength = -1 })
                .Should().Throw<ArgumentOutOfRangeException>().WithParameterName(nameof(RtmpChunkReaderOptions.MaxMessageLength));
            FluentActions.Invoking(() => new RtmpChunkReaderOptions { MaxMessageLength = RtmpChunkReaderOptions.HardMaxMessageLength + 1 })
                .Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => new RtmpChunkReaderOptions { MaxChunkStreams = 0 })
                .Should().Throw<ArgumentOutOfRangeException>().WithParameterName(nameof(RtmpChunkReaderOptions.MaxChunkStreams));
            FluentActions.Invoking(() => new RtmpChunkReaderOptions { MaxChunkStreams = RtmpChunkReaderOptions.HardMaxChunkStreams + 1 })
                .Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => new RtmpChunkReaderOptions { MaxBufferedBytes = 0 })
                .Should().Throw<ArgumentOutOfRangeException>().WithParameterName(nameof(RtmpChunkReaderOptions.MaxBufferedBytes));
            FluentActions.Invoking(() => new RtmpChunkReaderOptions { MaxBufferedBytes = RtmpChunkReaderOptions.HardMaxBufferedBytes + 1 })
                .Should().Throw<ArgumentOutOfRangeException>();
        }
    }
}
