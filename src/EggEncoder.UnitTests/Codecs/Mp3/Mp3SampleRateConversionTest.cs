using EggEncoder.Codecs;
using EggEncoder.Codecs.Mp3;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Mp3
{
    public class Mp3SampleRateConversionTest
    {
        private const double ToneFrequency = 1000;

        [Theory]
        [InlineData(96000, 24, 2, 192_038, 48000, 96_019)]
        [InlineData(88200, 24, 2, 176_402, 44100, 88_201)]
        [InlineData(192000, 16, 2, 384_004, 48000, 96_001)]
        [InlineData(22050, 16, 1, 44_101, 44100, 88_202)]
        public void Convert_WavAtARateMp3DoesNotSupport_Should_ResampleToTheNearestMp3Rate_WithTheExactResampledLength(
            int sourceSampleRate,
            int bitsPerSample,
            int channels,
            int sourceFrames,
            int expectedMp3SampleRate,
            int expectedFrames)
        {
            var amplitude = 0.5 * (1 << (bitsPerSample - 1));
            var source = BuildSine(amplitude, sourceFrames, channels, sourceSampleRate);
            var wavPath = Mp3EncoderLevelTest.TempPath($"rate{sourceSampleRate}", ".wav");
            var mp3Path = Mp3EncoderLevelTest.TempPath($"rate{sourceSampleRate}");
            WavFileBuilder.Create(wavPath, channels, sourceSampleRate, bitsPerSample, source);

            try
            {
                AudioCutter.Convert(wavPath, mp3Path);

                var probe = Mp3Probe.Probe(mp3Path);
                probe.SampleRate.Should().Be(expectedMp3SampleRate);
                probe.Channels.Should().Be(channels);
                probe.DurationSeconds.Should().BeApproximately((double)expectedFrames / expectedMp3SampleRate, 1e-9);

                var (_, decoded) = Mp3TestDecoder.DecodeAll(mp3Path);
                var sourceAt16Bit = source.Select(sample => sample >> (bitsPerSample - 16)).ToArray();
                Mp3EncoderLevelTest.LevelDifferenceDb(sourceAt16Bit, decoded, channels, sourceSampleRate, expectedMp3SampleRate)
                    .Should().BeApproximately(0, 0.15);
            }
            finally
            {
                File.Delete(wavPath);
                File.Delete(mp3Path);
            }
        }

        [Fact]
        public void Convert_FullScale96kHzWav_Should_ClampTheResampledSignalInsteadOfWrappingAround()
        {
            const int bitsPerSample = 24;
            var source = BuildSine((1 << (bitsPerSample - 1)) - 1, frameCount: 96_000, channels: 2, sampleRate: 96000);
            var wavPath = Mp3EncoderLevelTest.TempPath("fullscale96k", ".wav");
            var mp3Path = Mp3EncoderLevelTest.TempPath("fullscale96k");
            WavFileBuilder.Create(wavPath, channels: 2, sampleRate: 96000, bitsPerSample, source);

            try
            {
                AudioCutter.Convert(wavPath, mp3Path);

                var (_, decoded) = Mp3TestDecoder.DecodeAll(mp3Path);

                Mp3EncoderLevelTest.LargestStepBetweenNeighbors(decoded, channels: 2).Should().BeLessThan(10000);
            }
            finally
            {
                File.Delete(wavPath);
                File.Delete(mp3Path);
            }
        }

        [Fact]
        public void OpenSession_AtARateMp3DoesNotSupport_Should_Throw_BecauseOnlyConversionsResample()
        {
            var mp3Path = Mp3EncoderLevelTest.TempPath("strict96k");

            try
            {
                var openSession = () => Mp3Encoder.OpenSession(mp3Path, channels: 2, sampleRate: 96000, bitsPerSample: 24);

                openSession.Should().Throw<NotSupportedException>();
            }
            finally
            {
                File.Delete(mp3Path);
            }
        }

        private static int[] BuildSine(double amplitude, int frameCount, int channels, int sampleRate)
        {
            var samples = new int[frameCount * channels];
            for (var frame = 0; frame < frameCount; frame++)
            {
                var value = (int)Math.Round(amplitude * Math.Sin(2 * Math.PI * ToneFrequency * frame / sampleRate));
                for (var channel = 0; channel < channels; channel++)
                {
                    samples[(frame * channels) + channel] = value;
                }
            }

            return samples;
        }
    }
}
