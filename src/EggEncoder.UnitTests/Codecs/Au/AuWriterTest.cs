using EggEncoder.Codecs;
using EggEncoder.Codecs.Au;
using FluentAssertions;
using System.Buffers.Binary;
using System.Text;

namespace EggEncoder.UnitTests.Codecs.Au
{
    public class AuWriterTest
    {
        // -------- Stream-based Create --------

        [Fact]
        public void Create_Stream_Should_Round_Trip_Through_AuReader_Same_As_Path_Based_Create()
        {
            var samples = new[] { 0, 1000, -1000, 32767, -32768 };

            using var memoryStream = new MemoryStream();
            using (var writer = AuWriter.Create(memoryStream, channels: 1, sampleRate: 8000, bitsPerSample: 16, totalFrames: samples.Length))
            {
                writer.WriteInterleavedSamples(samples, samples.Length);
            }

            memoryStream.Position = 0;
            using var reader = AuReader.Open(memoryStream);

            reader.Channels.Should().Be(1);
            reader.SampleRate.Should().Be(8000);
            reader.BitsPerSample.Should().Be(16);

            var buffer = new int[samples.Length];
            reader.ReadInterleavedSamples(buffer, samples.Length);
            buffer.Should().Equal(samples);
        }

        [Fact]
        public void Create_Stream_Should_Produce_TheExactSameBytes_As_PathBased_Create()
        {
            var samples = new[] { 0, 500, -500, 32000, -32000, 100, -100, 7777 };
            var filePath = Path.GetTempFileName();
            try
            {
                using (var writer = AuWriter.Create(filePath, channels: 2, sampleRate: 44100, bitsPerSample: 16, totalFrames: samples.Length / 2, AuSampleFormat.ALaw))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length / 2);
                }

                var pathBasedBytes = File.ReadAllBytes(filePath);

                using var memoryStream = new MemoryStream();
                using (var writer = AuWriter.Create(memoryStream, channels: 2, sampleRate: 44100, bitsPerSample: 16, totalFrames: samples.Length / 2, AuSampleFormat.ALaw))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length / 2);
                }

                memoryStream.ToArray().Should().Equal(pathBasedBytes);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Create_Stream_Then_Dispose_Should_NotDispose_TheCallersStream()
        {
            using var memoryStream = new MemoryStream();
            var writer = AuWriter.Create(memoryStream, channels: 1, sampleRate: 8000, bitsPerSample: 16, totalFrames: 1);
            writer.WriteInterleavedSamples([0], 1);
            writer.Dispose();

            var act = () => memoryStream.Position;
            act.Should().NotThrow();
        }

        [Fact]
        public void Create_Stream_WithNullStream_Should_Throw()
        {
            var act = () => AuWriter.Create((Stream)null!, channels: 1, sampleRate: 8000, bitsPerSample: 16, totalFrames: 1);
            act.Should().Throw<ArgumentNullException>();
        }

        [Theory]
        [InlineData(8, new[] { 0, 127, -128, -64 })]
        [InlineData(16, new[] { 0, short.MaxValue, short.MinValue, -1 })]
        [InlineData(24, new[] { 0, 8388607, -8388608, -1 })]
        [InlineData(32, new[] { 0, int.MaxValue, int.MinValue, -12345678 })]
        public void Create_WithIntegerFormat_Should_Round_Trip_AtEveryBitDepth(int bitsPerSample, int[] samples)
        {
            var filePath = Path.GetTempFileName();
            try
            {
                using (var writer = AuWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample, totalFrames: samples.Length))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                }

                using var reader = AuReader.Open(filePath);
                reader.BitsPerSample.Should().Be(bitsPerSample);
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
        public void Create_WithAuSampleFormatInteger_Should_Write_ByteForByte_Identical_To_FiveParameterOverload()
        {
            var fivePath = Path.GetTempFileName();
            var sixPath = Path.GetTempFileName();
            try
            {
                var samples = new[] { 0, short.MaxValue, short.MinValue, -1 };

                using (var writer = AuWriter.Create(fivePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: samples.Length))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                }

                using (var writer = AuWriter.Create(sixPath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: samples.Length, AuSampleFormat.Integer))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                }

                File.ReadAllBytes(sixPath).Should().Equal(File.ReadAllBytes(fivePath));
            }
            finally
            {
                File.Delete(fivePath);
                File.Delete(sixPath);
            }
        }

        [Fact]
        public void Create_WithFloat32_Should_Round_Trip_Through_AuReader()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new[] { 0, int.MaxValue, int.MinValue, -1073741824 };

                using (var writer = AuWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 32, totalFrames: samples.Length, AuSampleFormat.Float32))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                }

                using var reader = AuReader.Open(filePath);
                reader.IsFloat32.Should().BeTrue();
                reader.BitsPerSample.Should().Be(32);

                var buffer = new int[samples.Length];
                reader.ReadInterleavedSamples(buffer, samples.Length);

                // Same float32-narrowing precision characteristics as AIFC's own fl32 (see
                // AiffWriterTest's matching test): int.MinValue clamps to MinValue+1 (no positive
                // counterpart at this scale), and -1073741824 loses its last bit through the float32
                // narrowing step specifically (verified independently in Python before writing this).
                buffer[0].Should().Be(0);
                buffer[1].Should().Be(int.MaxValue);
                buffer[2].Should().Be(int.MinValue + 1);
                buffer[3].Should().Be(-1073741823);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Create_WithFloat64_Should_Round_Trip_Through_AuReader()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new[] { 0, int.MaxValue, int.MinValue, -1073741824 };

                using (var writer = AuWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 32, totalFrames: samples.Length, AuSampleFormat.Float64))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                }

                using var reader = AuReader.Open(filePath);
                reader.IsFloat64.Should().BeTrue();
                reader.BitsPerSample.Should().Be(32);

                var buffer = new int[samples.Length];
                reader.ReadInterleavedSamples(buffer, samples.Length);

                // Unlike Float32, -1073741824 round-trips exactly here -- no float-narrowing step to
                // lose precision at (see Create_WithFloat64_Should_Preserve_More_Precision_Than_Float32
                // for a value chosen specifically to demonstrate this).
                buffer[0].Should().Be(0);
                buffer[1].Should().Be(int.MaxValue);
                buffer[2].Should().Be(int.MinValue + 1);
                buffer[3].Should().Be(-1073741824);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Create_WithFloat64_Should_Preserve_More_Precision_Than_Float32()
        {
            var sample = 123456789;

            var narrowed = (float)(sample / (double)int.MaxValue);
            var roundTrippedThroughFloat32 = (int)(Math.Clamp((double)narrowed, -1.0, 1.0) * int.MaxValue);
            roundTrippedThroughFloat32.Should().NotBe(sample, "the test's own premise requires float32 to actually lose precision for this value");

            var filePath = Path.GetTempFileName();
            try
            {
                using (var writer = AuWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 32, totalFrames: 1, AuSampleFormat.Float64))
                {
                    writer.WriteInterleavedSamples([sample], 1);
                }

                using var reader = AuReader.Open(filePath);
                var buffer = new int[1];
                reader.ReadInterleavedSamples(buffer, 1);

                buffer[0].Should().Be(sample);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Theory]
        [InlineData(AuSampleFormat.ALaw)]
        [InlineData(AuSampleFormat.MuLaw)]
        public void Create_WithG711Format_Should_Round_Trip_Through_AuReader(AuSampleFormat sampleFormat)
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new[] { 0, 1000, -1000, 8000, -8000 };

                using (var writer = AuWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: samples.Length, sampleFormat))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                }

                using var reader = AuReader.Open(filePath);
                reader.BitsPerSample.Should().Be(16);
                (sampleFormat == AuSampleFormat.ALaw ? reader.IsALaw : reader.IsMuLaw).Should().BeTrue();

                var buffer = new int[samples.Length];
                reader.ReadInterleavedSamples(buffer, samples.Length);

                var expected = samples.Select(s => sampleFormat == AuSampleFormat.ALaw
                    ? EggEncoder.Codecs.Wav.G711Codec.DecodeALaw(EggEncoder.Codecs.Wav.G711Codec.EncodeALaw(s))
                    : EggEncoder.Codecs.Wav.G711Codec.DecodeMuLaw(EggEncoder.Codecs.Wav.G711Codec.EncodeMuLaw(s))).ToArray();
                buffer.Should().Equal(expected);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Create_Should_Write_Expected_HeaderBytes_With_No_Annotation()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                using (var writer = AuWriter.Create(filePath, channels: 2, sampleRate: 8000, bitsPerSample: 16, totalFrames: 1))
                {
                    writer.WriteInterleavedSamples([1, -1], 1);
                }

                var bytes = File.ReadAllBytes(filePath);

                Encoding.ASCII.GetString(bytes, 0, 4).Should().Be(".snd");
                BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4, 4)).Should().Be(24, "no annotation string is ever written");
                BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)).Should().Be(4, "2 channels * 2 bytes/sample * 1 frame");
                BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(12, 4)).Should().Be(3, "encoding 3 = 16-bit signed PCM");
                BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)).Should().Be(8000);
                BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)).Should().Be(2);

                bytes.Length.Should().Be(28, "24-byte header + 4 bytes of sample data, no pad byte at all");
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
                using (var writer = AuWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: 2))
                {
                    writer.WriteInterleavedSamples([1, 2], frameCount: 0);
                    writer.WriteInterleavedSamples([1, 2], frameCount: -1);
                    writer.WriteInterleavedSamples([1, 2], frameCount: 2);
                }

                using var reader = AuReader.Open(filePath);
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
        public void WriteInterleavedSamples_Called_Repeatedly_With_Varying_Sizes_Should_Not_Leak_Stale_Bytes()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var firstBlock = new[] { 1, 2, 3, 4, 5, 6 };
                var secondBlock = new[] { 7, 8 };

                using (var writer = AuWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: firstBlock.Length + secondBlock.Length))
                {
                    writer.WriteInterleavedSamples(firstBlock, firstBlock.Length);
                    writer.WriteInterleavedSamples(secondBlock, secondBlock.Length);
                }

                using var reader = AuReader.Open(filePath);
                var buffer = new int[firstBlock.Length + secondBlock.Length];
                reader.ReadInterleavedSamples(buffer, buffer.Length);

                buffer.Should().Equal(1, 2, 3, 4, 5, 6, 7, 8);
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
        public void Create_WithUnsupportedBitsPerSample_Should_Throw(int bitsPerSample)
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var act = () => AuWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample, totalFrames: 1);

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Theory]
        [InlineData(AuSampleFormat.Float32, 16)]
        [InlineData(AuSampleFormat.Float64, 16)]
        [InlineData(AuSampleFormat.ALaw, 8)]
        [InlineData(AuSampleFormat.MuLaw, 32)]
        public void Create_WithWrongBitsPerSampleForFormat_Should_Throw(AuSampleFormat sampleFormat, int bitsPerSample)
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var act = () => AuWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample, totalFrames: 1, sampleFormat);

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }
    }
}
