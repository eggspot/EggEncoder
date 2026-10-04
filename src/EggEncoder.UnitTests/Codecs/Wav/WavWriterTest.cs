using EggEncoder.Codecs;
using EggEncoder.Codecs.Wav;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Wav
{
    public class WavWriterTest
    {
        [Fact]
        public void WriteInterleavedSamples_8Bit_Should_Round_Trip_Through_WavReader()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new[] { 0, 127, -128, -64 };

                using (var writer = WavWriter.Create(filePath, channels: 1, sampleRate: 8000, bitsPerSample: 8, totalFrames: samples.Length))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                }

                using var reader = WavReader.Open(filePath);
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
        public void WriteInterleavedSamples_32Bit_Should_Round_Trip_Through_WavReader()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new[] { 0, int.MaxValue, int.MinValue, -12345678 };

                using (var writer = WavWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 32, totalFrames: samples.Length))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                }

                using var reader = WavReader.Open(filePath);
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
        public void WriteInterleavedSamples_Float_Should_Round_Trip_Through_WavReader_As_Float()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                // Values chosen to round-trip exactly through actual IEEE-float bytes on disk: 0 and
                // +/-int.MaxValue map to +/-1.0f exactly (see WavWriter.Int32ToFloat32 / WavReader.Float32ToInt32).
                // Arbitrary large magnitudes (e.g. int.MinValue) are not bit-exact through float32 storage --
                // that's expected precision loss from the format, not tested here.
                var samples = new[] { 0, int.MaxValue, -int.MaxValue };

                using (var writer = WavWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 32, totalFrames: samples.Length, WavSampleFormat.Float32))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                }

                using var reader = WavReader.Open(filePath);
                reader.IsFloatFormat.Should().BeTrue();
                reader.BitsPerSample.Should().Be(32);

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
        public void Create_FloatFormat_With_NonThirtyTwoBit_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var act = () => WavWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: 1, WavSampleFormat.Float32);

                act.Should().Throw<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Theory]
        [InlineData(WavSampleFormat.MuLaw)]
        [InlineData(WavSampleFormat.ALaw)]
        public void Create_G711_With_NonSixteenBit_Should_Throw(WavSampleFormat sampleFormat)
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var act = () => WavWriter.Create(filePath, channels: 1, sampleRate: 8000, bitsPerSample: 8, totalFrames: 1, sampleFormat);

                act.Should().Throw<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Theory]
        [InlineData(WavSampleFormat.MuLaw)]
        [InlineData(WavSampleFormat.ALaw)]
        public void WriteInterleavedSamples_G711_Should_Round_Trip_Through_WavReader(WavSampleFormat sampleFormat)
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new[] { 0, 32767, -32768, 1000, -1000, 12345, -12345 };
                var expected = samples.Select(sample => sampleFormat == WavSampleFormat.MuLaw
                    ? G711Codec.DecodeMuLaw(G711Codec.EncodeMuLaw(sample))
                    : G711Codec.DecodeALaw(G711Codec.EncodeALaw(sample))).ToArray();

                using (var writer = WavWriter.Create(filePath, channels: 1, sampleRate: 8000, bitsPerSample: 16, totalFrames: samples.Length, sampleFormat))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                }

                using var reader = WavReader.Open(filePath);
                reader.BitsPerSample.Should().Be(16);
                reader.TotalSamples.Should().Be(samples.Length);
                reader.IsMuLaw.Should().Be(sampleFormat == WavSampleFormat.MuLaw);
                reader.IsALaw.Should().Be(sampleFormat == WavSampleFormat.ALaw);

                var buffer = new int[samples.Length];
                reader.ReadInterleavedSamples(buffer, samples.Length);

                buffer.Should().Equal(expected);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Theory]
        [InlineData(WavSampleFormat.MuLaw, "sample_g711_mulaw_mono.wav", "sample_g711_mulaw_mono_expected.pcm")]
        [InlineData(WavSampleFormat.ALaw, "sample_g711_alaw_mono.wav", "sample_g711_alaw_mono_expected.pcm")]
        public void WriteInterleavedSamples_G711_Should_Produce_BitExact_Bytes_Against_FfmpegEncodedFixture(WavSampleFormat sampleFormat, string fixtureFileName, string expectedPcmFileName)
        {
            // The strongest possible encode check: feed ffmpeg's own ground-truth decoded samples
            // back through this project's encoder and confirm the resulting coded BYTES match
            // ffmpeg's own real encoder output exactly, byte for byte -- not just that our own
            // decode(encode(x)) composes correctly in isolation (already covered by G711CodecTest).
            var fixturePath = Path.GetFullPath($"Codecs/Wav/{fixtureFileName}");
            var expectedPcmPath = Path.GetFullPath($"Codecs/Wav/{expectedPcmFileName}");
            var groundTruthSamples = ReadGroundTruthPcm16(expectedPcmPath);

            var filePath = Path.GetTempFileName();
            try
            {
                using (var writer = WavWriter.Create(filePath, channels: 1, sampleRate: 8000, bitsPerSample: 16, totalFrames: groundTruthSamples.Length, sampleFormat))
                {
                    writer.WriteInterleavedSamples(groundTruthSamples, groundTruthSamples.Length);
                }

                var producedDataBytes = ReadDataChunkBytes(filePath);
                var realEncodedDataBytes = ReadDataChunkBytes(fixturePath);

                producedDataBytes.Should().Equal(realEncodedDataBytes);
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

                using (var writer = WavWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: firstBlock.Length + secondBlock.Length))
                {
                    writer.WriteInterleavedSamples(firstBlock, firstBlock.Length);
                    writer.WriteInterleavedSamples(secondBlock, secondBlock.Length);
                }

                using var reader = WavReader.Open(filePath);
                var buffer = new int[firstBlock.Length + secondBlock.Length];
                reader.ReadInterleavedSamples(buffer, buffer.Length);

                buffer.Should().Equal(1, 2, 3, 4, 5, 6, 7, 8);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        private static int[] ReadGroundTruthPcm16(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var samples = new int[bytes.Length / 2];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (short)(bytes[i * 2] | (bytes[(i * 2) + 1] << 8));
            }

            return samples;
        }

        private static byte[] ReadDataChunkBytes(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var dataIndex = 0;
            for (var i = 12; i < bytes.Length - 8; i++)
            {
                if (bytes[i] == 'd' && bytes[i + 1] == 'a' && bytes[i + 2] == 't' && bytes[i + 3] == 'a')
                {
                    dataIndex = i;
                    break;
                }
            }

            var dataSize = BitConverter.ToUInt32(bytes, dataIndex + 4);

            return bytes[(dataIndex + 8)..(int)(dataIndex + 8 + dataSize)];
        }
    }
}
