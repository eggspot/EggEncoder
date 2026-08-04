using EggEncoder.Codecs.Flac;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Flac
{
    public class FlacRoundTripTest
    {
        [Fact]
        public void Encode_Then_Decode_16Bit_Stereo_Should_Reproduce_Exact_Samples()
        {
            var interleavedSamples = GenerateRandomSamples(seed: 1, frameCount: 5000, channels: 2, minValue: short.MinValue, maxValue: short.MaxValue);
            AssertRoundTrip(channels: 2, sampleRate: 44100, bitsPerSample: 16, interleavedSamples);
        }

        [Fact]
        public void Encode_Then_Decode_24Bit_Mono_Should_Reproduce_Exact_Samples()
        {
            var interleavedSamples = GenerateRandomSamples(seed: 2, frameCount: 5000, channels: 1, minValue: -8388608, maxValue: 8388607);
            AssertRoundTrip(channels: 1, sampleRate: 48000, bitsPerSample: 24, interleavedSamples);
        }

        [Fact]
        public void Encode_Then_Decode_Silence_Should_Reproduce_Exact_Samples()
        {
            var interleavedSamples = new int[2 * 100];
            AssertRoundTrip(channels: 2, sampleRate: 44100, bitsPerSample: 16, interleavedSamples);
        }

        [Fact]
        public void Encode_Then_Decode_FullScaleValues_Should_Reproduce_Exact_Samples()
        {
            var interleavedSamples = new[] { short.MinValue, short.MaxValue, 0, 0, short.MaxValue, short.MinValue };
            AssertRoundTrip(channels: 2, sampleRate: 44100, bitsPerSample: 16, interleavedSamples);
        }

        private static void AssertRoundTrip(int channels, int sampleRate, int bitsPerSample, int[] interleavedSamples)
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var wavPath = Path.Combine(tempDirectory, "source.wav");
                var flacPath = Path.Combine(tempDirectory, "output.flac");

                WavFileBuilder.Create(wavPath, channels, sampleRate, bitsPerSample, interleavedSamples);
                FlacEncoder.Encode(wavPath, flacPath);

                var (streamInfo, decodedSamples) = FlacTestDecoder.DecodeAll(flacPath);

                streamInfo.Channels.Should().Be(channels);
                streamInfo.SampleRate.Should().Be(sampleRate);
                streamInfo.BitsPerSample.Should().Be(bitsPerSample);
                streamInfo.TotalSamples.Should().Be(interleavedSamples.Length / channels);
                decodedSamples.Should().Equal(interleavedSamples);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        private static int[] GenerateRandomSamples(int seed, int frameCount, int channels, int minValue, int maxValue)
        {
            var random = new Random(seed);
            var samples = new int[frameCount * channels];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = random.Next(minValue, maxValue);
            }

            return samples;
        }
    }
}
