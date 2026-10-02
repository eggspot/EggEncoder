using EggEncoder.Codecs.Aiff;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Aiff
{
    public class AiffWriterTest
    {
        [Fact]
        public void WriteInterleavedSamples_8Bit_Should_Round_Trip_Through_AiffReader()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new[] { 0, 127, -128, -64 };

                using (var writer = AiffWriter.Create(filePath, channels: 1, sampleRate: 8000, bitsPerSample: 8, totalFrames: samples.Length))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                }

                using var reader = AiffReader.Open(filePath);
                reader.Channels.Should().Be(1);
                reader.SampleRate.Should().Be(8000);
                reader.BitsPerSample.Should().Be(8);
                reader.TotalSamples.Should().Be(samples.Length);

                var buffer = new int[samples.Length];
                reader.ReadInterleavedSamples(buffer, samples.Length);

                buffer.Should().Equal(samples);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_16Bit_Should_Round_Trip_Through_AiffReader()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new[] { 0, short.MaxValue, short.MinValue, -1 };

                using (var writer = AiffWriter.Create(filePath, channels: 2, sampleRate: 44100, bitsPerSample: 16, totalFrames: samples.Length / 2))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length / 2);
                }

                using var reader = AiffReader.Open(filePath);
                var buffer = new int[samples.Length];
                reader.ReadInterleavedSamples(buffer, samples.Length / 2);

                buffer.Should().Equal(samples);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_24Bit_Should_Round_Trip_Through_AiffReader()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new[] { 0, 8388607, -8388608, -1 };

                using (var writer = AiffWriter.Create(filePath, channels: 1, sampleRate: 48000, bitsPerSample: 24, totalFrames: samples.Length))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                }

                using var reader = AiffReader.Open(filePath);
                var buffer = new int[samples.Length];
                reader.ReadInterleavedSamples(buffer, samples.Length);

                buffer.Should().Equal(samples);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_32Bit_Should_Round_Trip_Through_AiffReader()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new[] { 0, int.MaxValue, int.MinValue, -12345678 };

                using (var writer = AiffWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 32, totalFrames: samples.Length))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                }

                using var reader = AiffReader.Open(filePath);
                var buffer = new int[samples.Length];
                reader.ReadInterleavedSamples(buffer, samples.Length);

                buffer.Should().Equal(samples);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_Called_Repeatedly_With_Varying_Sizes_Should_Not_Leak_Stale_Bytes()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var firstBlock = new[] { 1, 2, 3, 4, 5, 6 };
                var secondBlock = new[] { 7, 8 };

                using (var writer = AiffWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: firstBlock.Length + secondBlock.Length))
                {
                    writer.WriteInterleavedSamples(firstBlock, firstBlock.Length);
                    writer.WriteInterleavedSamples(secondBlock, secondBlock.Length);
                }

                using var reader = AiffReader.Open(filePath);
                var buffer = new int[firstBlock.Length + secondBlock.Length];
                reader.ReadInterleavedSamples(buffer, buffer.Length);

                buffer.Should().Equal(1, 2, 3, 4, 5, 6, 7, 8);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_WithZeroOrNegativeFrameCount_Should_Write_Nothing()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                using (var writer = AiffWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: 2))
                {
                    writer.WriteInterleavedSamples([1, 2], frameCount: 0);
                    writer.WriteInterleavedSamples([1, 2], frameCount: -1);
                    writer.WriteInterleavedSamples([1, 2], frameCount: 2);
                }

                using var reader = AiffReader.Open(filePath);
                reader.TotalSamples.Should().Be(2);

                var buffer = new int[2];
                reader.ReadInterleavedSamples(buffer, 2);
                buffer.Should().Equal(1, 2);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Create_With_OddTotalDataSize_Should_Write_A_Pad_Byte_Without_Corrupting_The_File()
        {
            // 1 channel, 8-bit, 3 frames = 3 (odd) bytes of sample data -- exercises AiffWriter's IFF
            // pad-byte handling on Dispose. If the pad byte (or the FORM/SSND size fields that account
            // for it) were wrong, this would either throw on Open or read back the wrong sample count.
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new[] { 1, -1, 2 };

                using (var writer = AiffWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 8, totalFrames: samples.Length))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                }

                var fileBytes = File.ReadAllBytes(filePath);
                (fileBytes.Length % 2).Should().Be(0, "every IFF chunk, and so the whole file, must end on an even byte boundary");

                using var reader = AiffReader.Open(filePath);
                reader.TotalSamples.Should().Be(3);

                var buffer = new int[3];
                reader.ReadInterleavedSamples(buffer, 3).Should().Be(3);
                buffer.Should().Equal(samples);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Create_With_EvenTotalDataSize_Should_Not_Write_A_Pad_Byte()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new[] { 1, -1 };

                using (var writer = AiffWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 8, totalFrames: samples.Length))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                }

                // FORM header (12) + COMM chunk (8+18=26) + SSND chunk header (8) + offset/blockSize (8) + 2 data bytes, no pad.
                var expectedFileLength = 12 + 26 + 8 + 8 + 2;
                new FileInfo(filePath).Length.Should().Be(expectedFileLength);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(12)]
        [InlineData(33)]
        [InlineData(64)]
        public void Create_With_UnsupportedBitsPerSample_Should_Throw(int bitsPerSample)
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var act = () => AiffWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample, totalFrames: 1);

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Create_With_HighSampleRate_Should_Round_Trip_Via_IeeeExtendedFloat()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                using (var writer = AiffWriter.Create(filePath, channels: 1, sampleRate: 192000, bitsPerSample: 16, totalFrames: 1))
                {
                    writer.WriteInterleavedSamples([42], 1);
                }

                using var reader = AiffReader.Open(filePath);
                reader.SampleRate.Should().Be(192000);
            }
            finally
            {
                File.Delete(filePath);
            }
        }
    }
}
