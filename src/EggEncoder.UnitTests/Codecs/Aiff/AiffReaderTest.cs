using EggEncoder.Codecs.Aiff;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Aiff
{
    public class AiffReaderTest
    {
        [Fact]
        public void Open_16Bit_Stereo_Should_Read_Header_And_Samples()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var interleavedSamples = new[] { 0, 0, short.MaxValue, short.MinValue, -1, 1 };
                AiffFileBuilder.Create(filePath, channels: 2, sampleRate: 44100, bitsPerSample: 16, interleavedSamples);

                using var aiffReader = AiffReader.Open(filePath);

                aiffReader.Channels.Should().Be(2);
                aiffReader.SampleRate.Should().Be(44100);
                aiffReader.BitsPerSample.Should().Be(16);
                aiffReader.TotalSamples.Should().Be(3);

                var buffer = new int[interleavedSamples.Length];
                var framesRead = aiffReader.ReadInterleavedSamples(buffer, maxSamplesPerChannel: 3);

                framesRead.Should().Be(3);
                buffer.Should().Equal(interleavedSamples);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_24Bit_Mono_Should_Sign_Extend_Negative_Samples()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var interleavedSamples = new[] { 0, 8388607, -8388608, -1 };
                AiffFileBuilder.Create(filePath, channels: 1, sampleRate: 48000, bitsPerSample: 24, interleavedSamples);

                using var aiffReader = AiffReader.Open(filePath);

                var buffer = new int[interleavedSamples.Length];
                var framesRead = aiffReader.ReadInterleavedSamples(buffer, maxSamplesPerChannel: interleavedSamples.Length);

                framesRead.Should().Be(interleavedSamples.Length);
                buffer.Should().Equal(interleavedSamples);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_8Bit_Mono_Should_Keep_Samples_Signed()
        {
            // The opposite of WavReader's 8-bit convention -- AIFF's 8-bit samples are signed already,
            // so no unsigned-to-signed rebasing happens on the way in.
            var filePath = Path.GetTempFileName();
            try
            {
                var signedSamples = new[] { 0, 127, -128, -64 };
                AiffFileBuilder.Create(filePath, channels: 1, sampleRate: 8000, bitsPerSample: 8, signedSamples);

                using var aiffReader = AiffReader.Open(filePath);
                aiffReader.BitsPerSample.Should().Be(8);

                var buffer = new int[signedSamples.Length];
                var framesRead = aiffReader.ReadInterleavedSamples(buffer, maxSamplesPerChannel: signedSamples.Length);

                framesRead.Should().Be(signedSamples.Length);
                buffer.Should().Equal(signedSamples);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_32Bit_Mono_Should_Read_FullRange_Samples()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var interleavedSamples = new[] { 0, int.MaxValue, int.MinValue, -12345678 };
                AiffFileBuilder.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 32, interleavedSamples);

                using var aiffReader = AiffReader.Open(filePath);

                var buffer = new int[interleavedSamples.Length];
                var framesRead = aiffReader.ReadInterleavedSamples(buffer, maxSamplesPerChannel: interleavedSamples.Length);

                framesRead.Should().Be(interleavedSamples.Length);
                buffer.Should().Equal(interleavedSamples);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void ReadInterleavedSamples_Should_Return_Zero_At_End_Of_Stream()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                AiffFileBuilder.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, [1, 2, 3]);

                using var aiffReader = AiffReader.Open(filePath);
                var buffer = new int[3];

                aiffReader.ReadInterleavedSamples(buffer, maxSamplesPerChannel: 3);
                var framesReadAfterEnd = aiffReader.ReadInterleavedSamples(buffer, maxSamplesPerChannel: 3);

                framesReadAfterEnd.Should().Be(0);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void ReadInterleavedSamples_Called_Repeatedly_With_Varying_Sizes_Should_Not_Leak_Stale_Bytes()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                AiffFileBuilder.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, [1, 2, 3, 4, 5, 6, 7, 8]);

                using var aiffReader = AiffReader.Open(filePath);

                var firstBuffer = new int[6];
                var firstFramesRead = aiffReader.ReadInterleavedSamples(firstBuffer, maxSamplesPerChannel: 6);

                var secondBuffer = new int[2];
                var secondFramesRead = aiffReader.ReadInterleavedSamples(secondBuffer, maxSamplesPerChannel: 2);

                firstFramesRead.Should().Be(6);
                firstBuffer.Should().Equal(1, 2, 3, 4, 5, 6);
                secondFramesRead.Should().Be(2);
                secondBuffer.Should().Equal(7, 8);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_With_UnsupportedBitsPerSample_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                AiffFileBuilder.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 12, [0, 1]);

                var act = () => AiffReader.Open(filePath).Dispose();
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
            var filePath = Path.GetTempFileName();
            try
            {
                AiffFileBuilder.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, []);
                OverwriteChannelCount(filePath, 0);

                var act = () => AiffReader.Open(filePath).Dispose();
                act.Should().ThrowExactly<InvalidDataException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_Missing_FormHeader_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(filePath, "NOTF"u8.ToArray());

                var act = () => AiffReader.Open(filePath).Dispose();
                act.Should().ThrowExactly<InvalidDataException>().WithMessage("*missing FORM header*");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_NonAiffFormType_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write("FORM"u8);
                    writer.Write(0u);
                    writer.Write("AIFC"u8);
                }

                var act = () => AiffReader.Open(filePath).Dispose();
                act.Should().ThrowExactly<InvalidDataException>().WithMessage("*AIFC is not supported*");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_Missing_CommChunk_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write("FORM"u8);
                    writer.Write(0u);
                    writer.Write("AIFF"u8);
                }

                var act = () => AiffReader.Open(filePath).Dispose();
                act.Should().ThrowExactly<InvalidDataException>().WithMessage("*missing a 'COMM' chunk*");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_Missing_SsndChunk_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                AiffFileBuilder.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, [1, 2]);
                TruncateAfterCommChunk(filePath);

                var act = () => AiffReader.Open(filePath).Dispose();
                act.Should().ThrowExactly<InvalidDataException>().WithMessage("*missing an 'SSND' chunk*");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_With_OddSizedChunk_Should_Skip_The_Pad_Byte()
        {
            // 1 channel, 8-bit, 3 frames = 3 (odd) data bytes -- AiffFileBuilder writes the IFF pad byte.
            // If AiffReader didn't account for it, the chunk-walk would misalign on any chunk after SSND;
            // here SSND is last, so this only self-validates by reading the three samples back correctly.
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new[] { 1, -1, 2 };
                AiffFileBuilder.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 8, samples);

                using var aiffReader = AiffReader.Open(filePath);
                var buffer = new int[3];
                aiffReader.ReadInterleavedSamples(buffer, maxSamplesPerChannel: 3).Should().Be(3);
                buffer.Should().Equal(samples);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        private static void OverwriteChannelCount(string filePath, short channels)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite);
            stream.Seek(12 + 8, SeekOrigin.Begin); // FORM header (12) + COMM id/size (8), channels field follows immediately
            var bytes = new byte[2];
            System.Buffers.Binary.BinaryPrimitives.WriteInt16BigEndian(bytes, channels);
            stream.Write(bytes);
        }

        private static void TruncateAfterCommChunk(string filePath)
        {
            const int formHeaderSize = 12;
            const int commChunkTotalSize = 8 + 18;

            var bytes = File.ReadAllBytes(filePath);
            File.WriteAllBytes(filePath, bytes[..(formHeaderSize + commChunkTotalSize)]);
        }
    }
}
