using EggEncoder.Codecs.Wav;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Wav
{
    public class WavReaderTest
    {
        [Fact]
        public void Open_16Bit_Stereo_Should_Read_Header_And_Samples()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var interleavedSamples = new[] { 0, 0, short.MaxValue, short.MinValue, -1, 1 };
                WavFileBuilder.Create(filePath, channels: 2, sampleRate: 44100, bitsPerSample: 16, interleavedSamples);

                using var wavReader = WavReader.Open(filePath);

                wavReader.Channels.Should().Be(2);
                wavReader.SampleRate.Should().Be(44100);
                wavReader.BitsPerSample.Should().Be(16);
                wavReader.TotalSamples.Should().Be(3);

                var buffer = new int[interleavedSamples.Length];
                var framesRead = wavReader.ReadInterleavedSamples(buffer, maxSamplesPerChannel: 3);

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
                WavFileBuilder.Create(filePath, channels: 1, sampleRate: 48000, bitsPerSample: 24, interleavedSamples);

                using var wavReader = WavReader.Open(filePath);

                var buffer = new int[interleavedSamples.Length];
                var framesRead = wavReader.ReadInterleavedSamples(buffer, maxSamplesPerChannel: interleavedSamples.Length);

                framesRead.Should().Be(interleavedSamples.Length);
                buffer.Should().Equal(interleavedSamples);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_8Bit_Mono_Should_Convert_Unsigned_Bytes_To_Signed_Samples()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var unsignedBytes = new[] { 128, 255, 0, 64 };
                WavFileBuilder.Create(filePath, channels: 1, sampleRate: 8000, bitsPerSample: 8, unsignedBytes);

                using var wavReader = WavReader.Open(filePath);
                wavReader.BitsPerSample.Should().Be(8);

                var buffer = new int[unsignedBytes.Length];
                var framesRead = wavReader.ReadInterleavedSamples(buffer, maxSamplesPerChannel: unsignedBytes.Length);

                framesRead.Should().Be(unsignedBytes.Length);
                buffer.Should().Equal(0, 127, -128, -64);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_32BitFloat_Stereo_Should_Scale_To_Full_Int32_Range()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var floatSamples = new[] { 0f, 1f, -1f, 0.5f };
                WavFileBuilder.CreateFloat32(filePath, channels: 2, sampleRate: 44100, floatSamples);

                using var wavReader = WavReader.Open(filePath);
                wavReader.BitsPerSample.Should().Be(32);
                wavReader.Channels.Should().Be(2);

                var buffer = new int[floatSamples.Length];
                var framesRead = wavReader.ReadInterleavedSamples(buffer, maxSamplesPerChannel: 2);

                framesRead.Should().Be(2);
                buffer[0].Should().Be(0);
                buffer[1].Should().Be(int.MaxValue);
                buffer[2].Should().Be(-int.MaxValue);
                buffer[3].Should().Be(int.MaxValue / 2);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_32BitFloat_NaN_Should_Decode_As_Silence()
        {
            // A plain (int)double.NaN cast's result for an out-of-range value is unspecified by the C#
            // spec, so relying on it (whatever it happens to evaluate to on a given runtime) would be
            // fragile. A malformed or synthesized float WAV could still contain a NaN sample, so this is
            // handled explicitly instead, mapping it to silence (0).
            var filePath = Path.GetTempFileName();
            try
            {
                WavFileBuilder.CreateFloat32(filePath, channels: 1, sampleRate: 44100, [float.NaN]);

                using var wavReader = WavReader.Open(filePath);
                var buffer = new int[1];
                wavReader.ReadInterleavedSamples(buffer, maxSamplesPerChannel: 1);

                buffer[0].Should().Be(0);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Theory]
        [InlineData(float.PositiveInfinity, int.MaxValue)]
        [InlineData(float.NegativeInfinity, -int.MaxValue)]
        public void Open_32BitFloat_Infinity_Should_Clamp_To_FullScale(float input, int expected)
        {
            var filePath = Path.GetTempFileName();
            try
            {
                WavFileBuilder.CreateFloat32(filePath, channels: 1, sampleRate: 44100, [input]);

                using var wavReader = WavReader.Open(filePath);
                var buffer = new int[1];
                wavReader.ReadInterleavedSamples(buffer, maxSamplesPerChannel: 1);

                buffer[0].Should().Be(expected);
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
                WavFileBuilder.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, [1, 2, 3]);

                using var wavReader = WavReader.Open(filePath);
                var buffer = new int[3];

                wavReader.ReadInterleavedSamples(buffer, maxSamplesPerChannel: 3);
                var framesReadAfterEnd = wavReader.ReadInterleavedSamples(buffer, maxSamplesPerChannel: 3);

                framesReadAfterEnd.Should().Be(0);
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
                WavFileBuilder.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 12, [0, 1]);

                var act = () => WavReader.Open(filePath).Dispose();
                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }
    }
}
