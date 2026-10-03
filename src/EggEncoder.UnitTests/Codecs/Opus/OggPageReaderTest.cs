using System.Buffers.Binary;
using EggEncoder.Codecs.Opus;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Opus
{
    public class OggPageReaderTest
    {
        [Fact]
        public void ReadNextPacket_PacketSpanningTwoPages_Should_Reassemble_Correctly()
        {
            // OggPageWriter never actually produces this (one page always holds an entire Opus
            // packet -- see its own doc comment), but a reader has to handle it regardless for any
            // other real-world Ogg producer's output, so this hand-builds the two pages directly:
            // a 255-byte first segment (lacing value 255, "more data follows" with no in-page
            // terminator) continuing into a second page's 45-byte terminated segment.
            var firstPageData = new byte[255];
            Array.Fill(firstPageData, (byte)0xAA);
            var secondPageData = new byte[45];
            Array.Fill(secondPageData, (byte)0xBB);

            using var stream = new MemoryStream();
            WriteRawPage(stream, serialNumber: 1, sequenceNumber: 0, granulePosition: 0, headerTypeFlag: 0x02, lacingValues: [255], segmentData: firstPageData);
            WriteRawPage(stream, serialNumber: 1, sequenceNumber: 1, granulePosition: 300, headerTypeFlag: 0x04, lacingValues: [45], segmentData: secondPageData);

            stream.Position = 0;
            using var reader = new OggPageReader(stream);

            var packet = reader.ReadNextPacket();

            packet.Should().HaveCount(300);
            packet![..255].Should().OnlyContain(b => b == 0xAA);
            packet[255..].Should().OnlyContain(b => b == 0xBB);
            reader.ReadNextPacket().Should().BeNull();
        }

        [Fact]
        public void ReadNextPacket_WithMissingCapturePattern_Should_Throw()
        {
            using var stream = new MemoryStream();
            WriteRawPage(stream, serialNumber: 1, sequenceNumber: 0, granulePosition: 0, headerTypeFlag: 0x02, lacingValues: [1], segmentData: [9], corruptCapturePattern: true);

            stream.Position = 0;
            using var reader = new OggPageReader(stream);

            var act = () => reader.ReadNextPacket();

            act.Should().ThrowExactly<InvalidDataException>();
        }

        [Fact]
        public void ReadNextPacket_WithBadCrc_Should_Throw()
        {
            using var stream = new MemoryStream();
            WriteRawPage(stream, serialNumber: 1, sequenceNumber: 0, granulePosition: 0, headerTypeFlag: 0x02, lacingValues: [1], segmentData: [9], corruptCrc: true);

            stream.Position = 0;
            using var reader = new OggPageReader(stream);

            var act = () => reader.ReadNextPacket();

            act.Should().ThrowExactly<InvalidDataException>().WithMessage("*CRC32*");
        }

        [Fact]
        public void ReadNextPacket_WithTruncatedHeader_Should_Throw()
        {
            using var stream = new MemoryStream(new byte[10]); // far short of a 27-byte header
            using var reader = new OggPageReader(stream);

            var act = () => reader.ReadNextPacket();

            act.Should().ThrowExactly<InvalidDataException>();
        }

        [Fact]
        public void ReadNextPacket_WithTruncatedSegmentData_Should_Throw()
        {
            using var stream = new MemoryStream();
            WriteRawPage(stream, serialNumber: 1, sequenceNumber: 0, granulePosition: 0, headerTypeFlag: 0x02, lacingValues: [10], segmentData: [1, 2, 3]); // lacing claims 10 bytes, only 3 follow

            stream.Position = 0;
            using var reader = new OggPageReader(stream);

            var act = () => reader.ReadNextPacket();

            act.Should().Throw<Exception>(); // EndOfStreamException from the short ReadExactly
        }

        [Fact]
        public void ReadNextPacket_WithNoPagesAtAll_Should_ReturnNull()
        {
            using var stream = new MemoryStream();
            using var reader = new OggPageReader(stream);

            reader.ReadNextPacket().Should().BeNull();
        }

        [Fact]
        public void ReadNextPacket_StreamEndingMidPacket_Should_Throw()
        {
            // A page whose last lacing value is 255 (packet continues) but no further page ever
            // arrives -- the stream ends while a packet is still incomplete.
            var data = new byte[255];
            using var stream = new MemoryStream();
            WriteRawPage(stream, serialNumber: 1, sequenceNumber: 0, granulePosition: 0, headerTypeFlag: 0x02, lacingValues: [255], segmentData: data);

            stream.Position = 0;
            using var reader = new OggPageReader(stream);

            var act = () => reader.ReadNextPacket();

            act.Should().ThrowExactly<InvalidDataException>();
        }

        private static void WriteRawPage(
            Stream stream,
            uint serialNumber,
            uint sequenceNumber,
            long granulePosition,
            byte headerTypeFlag,
            int[] lacingValues,
            byte[] segmentData,
            bool corruptCapturePattern = false,
            bool corruptCrc = false)
        {
            var header = new byte[27];
            "OggS"u8.CopyTo(header);
            if (corruptCapturePattern)
            {
                header[0] = (byte)'X';
            }

            header[4] = 0;
            header[5] = headerTypeFlag;
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(6, 8), granulePosition);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14, 4), serialNumber);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(18, 4), sequenceNumber);
            header[26] = (byte)lacingValues.Length;

            var lacingBytes = new byte[lacingValues.Length];
            for (var i = 0; i < lacingValues.Length; i++)
            {
                lacingBytes[i] = (byte)lacingValues[i];
            }

            var crc = OggCrc32.Compute([.. header, .. lacingBytes, .. segmentData]);
            if (corruptCrc)
            {
                crc ^= 0xFFFFFFFFu;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(22, 4), crc);

            stream.Write(header);
            stream.Write(lacingBytes);
            stream.Write(segmentData);
        }
    }
}
