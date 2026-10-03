using EggEncoder.Codecs.Opus;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Opus
{
    public class OggPageWriterTest
    {
        [Fact]
        public void WritePacket_Then_OggPageReader_Should_RoundTrip_SinglePacket()
        {
            using var stream = new MemoryStream();
            var writer = new OggPageWriter(stream, serialNumber: 1);

            writer.WritePacket([1, 2, 3, 4, 5], granulePosition: 100, isEndOfStream: true);

            stream.Position = 0;
            using var reader = new OggPageReader(stream);

            reader.ReadNextPacket().Should().Equal(1, 2, 3, 4, 5);
            reader.ReadNextPacket().Should().BeNull();
        }

        [Fact]
        public void WritePacket_Then_OggPageReader_Should_RoundTrip_MultiplePackets()
        {
            using var stream = new MemoryStream();
            var writer = new OggPageWriter(stream, serialNumber: 42);

            writer.WritePacket([1, 2, 3], granulePosition: 10, isEndOfStream: false);
            writer.WritePacket([4, 5], granulePosition: 20, isEndOfStream: false);
            writer.WritePacket([6], granulePosition: 30, isEndOfStream: true);

            stream.Position = 0;
            using var reader = new OggPageReader(stream);

            reader.ReadNextPacket().Should().Equal(1, 2, 3);
            reader.ReadNextPacket().Should().Equal(4, 5);
            reader.ReadNextPacket().Should().Equal(6);
            reader.ReadNextPacket().Should().BeNull();
        }

        [Fact]
        public void WritePacket_Should_RoundTrip_EmptyPacket()
        {
            using var stream = new MemoryStream();
            var writer = new OggPageWriter(stream, serialNumber: 1);

            writer.WritePacket([], granulePosition: 0, isEndOfStream: true);

            stream.Position = 0;
            using var reader = new OggPageReader(stream);

            reader.ReadNextPacket().Should().BeEmpty();
        }

        [Theory]
        [InlineData(254)]
        [InlineData(255)]
        [InlineData(256)]
        [InlineData(510)]
        [InlineData(1275)] // Opus's own maximum packet size
        public void WritePacket_Should_RoundTrip_PacketsAcrossTheLacingBoundary(int packetLength)
        {
            var packet = new byte[packetLength];
            for (var i = 0; i < packetLength; i++)
            {
                packet[i] = (byte)(i % 256);
            }

            using var stream = new MemoryStream();
            var writer = new OggPageWriter(stream, serialNumber: 1);
            writer.WritePacket(packet, granulePosition: 0, isEndOfStream: true);

            stream.Position = 0;
            using var reader = new OggPageReader(stream);

            reader.ReadNextPacket().Should().Equal(packet);
        }

        [Fact]
        public void WritePacket_FirstPage_Should_SetBeginningOfStreamFlag()
        {
            using var stream = new MemoryStream();
            var writer = new OggPageWriter(stream, serialNumber: 1);

            writer.WritePacket([1], granulePosition: 0, isEndOfStream: false);
            writer.WritePacket([2], granulePosition: 0, isEndOfStream: false);

            var bytes = stream.ToArray();
            bytes[5].Should().Be(0x02, "the first page must have the beginning-of-stream flag set");

            var secondPageStart = 27 + 1 + 1; // header + 1 lacing byte + 1-byte first packet
            bytes[secondPageStart + 5].Should().Be(0x00, "only the first page should have the bos flag");
        }

        [Fact]
        public void WritePacket_LastPage_Should_SetEndOfStreamFlag()
        {
            using var stream = new MemoryStream();
            var writer = new OggPageWriter(stream, serialNumber: 1);

            writer.WritePacket([1], granulePosition: 0, isEndOfStream: true);

            var bytes = stream.ToArray();
            (bytes[5] & 0x04).Should().NotBe(0, "the final page must have the end-of-stream flag set");
        }

        [Fact]
        public void WritePacket_Should_IncrementPageSequenceNumber()
        {
            using var stream = new MemoryStream();
            var writer = new OggPageWriter(stream, serialNumber: 1);

            writer.WritePacket([1], granulePosition: 0, isEndOfStream: false);
            writer.WritePacket([2], granulePosition: 0, isEndOfStream: true);

            var bytes = stream.ToArray();
            BitConverterLittleEndianUInt32(bytes, 18).Should().Be(0u);

            var secondPageStart = 27 + 1 + 1;
            BitConverterLittleEndianUInt32(bytes, secondPageStart + 18).Should().Be(1u);
        }

        private static uint BitConverterLittleEndianUInt32(byte[] bytes, int offset) =>
            (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));
    }
}
