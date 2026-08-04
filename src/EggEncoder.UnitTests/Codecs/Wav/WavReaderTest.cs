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
                WavFileBuilder.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 8, [0, 1]);

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
