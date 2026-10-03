using System.Buffers.Binary;
using EggEncoder.Codecs.Tta;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Tta
{
    public class TtaReaderTest
    {
        [Fact]
        public void Open_Missing_Tta1Header_Should_Throw()
        {
            var filePath = WriteBytes("NOPE"u8.ToArray());
            try
            {
                var act = () => TtaReader.Open(filePath).Dispose();
                act.Should().ThrowExactly<InvalidDataException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_With_BadHeaderCrc_Should_Throw()
        {
            var header = BuildHeader(format: 1, channels: 1, bitsPerSample: 16, sampleRate: 245, totalSamples: 0);
            var bytes = header.Concat(Uint32LittleEndianBytes(0xDEADBEEFu)).ToArray(); // wrong CRC

            var filePath = WriteBytes(bytes);
            try
            {
                var act = () => TtaReader.Open(filePath).Dispose();
                act.Should().ThrowExactly<InvalidDataException>().WithMessage("*CRC32*");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_With_UnsupportedFormat_Should_Throw()
        {
            var filePath = WriteBytes(BuildValidHeaderWithCrc(format: 2, channels: 1, bitsPerSample: 16, sampleRate: 245, totalSamples: 0));
            try
            {
                var act = () => TtaReader.Open(filePath).Dispose();
                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_With_ZeroChannels_Should_Throw()
        {
            var filePath = WriteBytes(BuildValidHeaderWithCrc(format: 1, channels: 0, bitsPerSample: 16, sampleRate: 245, totalSamples: 0));
            try
            {
                var act = () => TtaReader.Open(filePath).Dispose();
                act.Should().ThrowExactly<InvalidDataException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_With_TruncatedSeekTable_Should_Throw()
        {
            // totalSamples=700 at sampleRate=245 (frameLength=256) needs 3 seek-table entries (12
            // bytes + 4-byte CRC = 16 bytes), but only 4 bytes follow the header here.
            var bytes = BuildValidHeaderWithCrc(format: 1, channels: 1, bitsPerSample: 16, sampleRate: 245, totalSamples: 700)
                .Concat(new byte[4])
                .ToArray();

            var filePath = WriteBytes(bytes);
            try
            {
                var act = () => TtaReader.Open(filePath).Dispose();
                act.Should().ThrowExactly<InvalidDataException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_With_BadSeekTableCrc_Should_Throw()
        {
            var header = BuildValidHeaderWithCrc(format: 1, channels: 1, bitsPerSample: 16, sampleRate: 245, totalSamples: 100);
            var seekTable = new byte[4]; // 1 frame (100 samples < 256 frameLength)
            BinaryPrimitives.WriteUInt32LittleEndian(seekTable, 10); // declares a 10-byte frame
            var bytes = header.Concat(seekTable).Concat(Uint32LittleEndianBytes(0xDEADBEEFu)).ToArray();

            var filePath = WriteBytes(bytes);
            try
            {
                var act = () => TtaReader.Open(filePath).Dispose();
                act.Should().ThrowExactly<InvalidDataException>().WithMessage("*seek table*");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void ReadNextFrame_With_ImpossibleSize_Should_Throw()
        {
            var header = BuildValidHeaderWithCrc(format: 1, channels: 1, bitsPerSample: 16, sampleRate: 245, totalSamples: 100);
            var seekTable = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(seekTable, 2); // impossible: < 4 (no room for the frame's own CRC)
            var bytes = header.Concat(seekTable).Concat(Uint32LittleEndianBytes(Crc32.Compute(seekTable))).ToArray();

            var filePath = WriteBytes(bytes);
            try
            {
                using var reader = TtaReader.Open(filePath);
                var act = () => reader.ReadNextFrame();
                act.Should().ThrowExactly<InvalidDataException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void ReadNextFrame_With_BadFrameCrc_Should_Throw()
        {
            var header = BuildValidHeaderWithCrc(format: 1, channels: 1, bitsPerSample: 16, sampleRate: 245, totalSamples: 100);
            var seekTable = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(seekTable, 8); // 4 data bytes + 4 CRC bytes
            var bytes = header
                .Concat(seekTable)
                .Concat(Uint32LittleEndianBytes(Crc32.Compute(seekTable)))
                .Concat(new byte[] { 1, 2, 3, 4 })
                .Concat(Uint32LittleEndianBytes(0xDEADBEEFu)) // wrong CRC for the frame data
                .ToArray();

            var filePath = WriteBytes(bytes);
            try
            {
                using var reader = TtaReader.Open(filePath);
                var act = () => reader.ReadNextFrame();
                act.Should().ThrowExactly<InvalidDataException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void ReadNextFrame_With_TruncatedFrameData_Should_Throw()
        {
            var header = BuildValidHeaderWithCrc(format: 1, channels: 1, bitsPerSample: 16, sampleRate: 245, totalSamples: 100);
            var seekTable = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(seekTable, 100); // declares 100 bytes, but none follow
            var bytes = header.Concat(seekTable).Concat(Uint32LittleEndianBytes(Crc32.Compute(seekTable))).ToArray();

            var filePath = WriteBytes(bytes);
            try
            {
                using var reader = TtaReader.Open(filePath);
                var act = () => reader.ReadNextFrame();
                act.Should().ThrowExactly<InvalidDataException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void ReadNextFrame_When_NoFramesRemain_Should_Throw()
        {
            var bytes = BuildValidHeaderWithCrc(format: 1, channels: 1, bitsPerSample: 16, sampleRate: 245, totalSamples: 0)
                .Concat(Uint32LittleEndianBytes(Crc32.Compute([])))
                .ToArray();

            var filePath = WriteBytes(bytes);
            try
            {
                using var reader = TtaReader.Open(filePath);
                var act = () => reader.ReadNextFrame();
                act.Should().ThrowExactly<InvalidOperationException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_With_ValidFile_Should_ExposeCorrectMetadata()
        {
            var bytes = BuildValidHeaderWithCrc(format: 1, channels: 2, bitsPerSample: 16, sampleRate: 245, totalSamples: 0)
                .Concat(Uint32LittleEndianBytes(Crc32.Compute([])))
                .ToArray();

            var filePath = WriteBytes(bytes);
            try
            {
                using var reader = TtaReader.Open(filePath);
                reader.Channels.Should().Be(2);
                reader.BitsPerSample.Should().Be(16);
                reader.SampleRate.Should().Be(245);
                reader.TotalSamples.Should().Be(0);
                reader.FrameLength.Should().Be(256);
                reader.HasMoreFrames.Should().BeFalse();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        private static string WriteBytes(byte[] bytes)
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"tta_reader_{Guid.NewGuid():N}.tta");
            File.WriteAllBytes(filePath, bytes);
            return filePath;
        }

        private static byte[] BuildHeader(ushort format, ushort channels, ushort bitsPerSample, uint sampleRate, uint totalSamples)
        {
            var header = new byte[18];
            "TTA1"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), format);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), channels);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), bitsPerSample);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(10), sampleRate);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14), totalSamples);
            return header;
        }

        private static byte[] BuildValidHeaderWithCrc(ushort format, ushort channels, ushort bitsPerSample, uint sampleRate, uint totalSamples)
        {
            var header = BuildHeader(format, channels, bitsPerSample, sampleRate, totalSamples);
            return header.Concat(Uint32LittleEndianBytes(Crc32.Compute(header))).ToArray();
        }

        // TTA's multi-byte fields are explicitly little-endian; BitConverter.GetBytes' byte order
        // depends on host platform endianness, so this (matching the production code's own use of
        // BinaryPrimitives throughout) is the portable way to build one of those fields by hand.
        private static byte[] Uint32LittleEndianBytes(uint value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            return bytes;
        }
    }
}
