using EggEncoder.Codecs.Mp3;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Mp3
{
    public class Mp3EncoderLevelTest
    {
        private const int SampleRate = 44100;
        private const double ToneFrequency = 1000;
        private const double LevelToleranceDb = 0.15;

        [Theory]
        [InlineData(-6.0)]
        [InlineData(-20.0)]
        public void Encode_SineTone_Should_DecodeAtTheSourceLevel_WithoutGainFitting(double levelDbfs)
        {
            var amplitude = 32767 * Math.Pow(10, levelDbfs / 20);
            var source = BuildStereoSine(amplitude, SampleRate * 2);
            var mp3Path = TempPath("level");

            try
            {
                using (var session = Mp3Encoder.OpenSession(mp3Path, channels: 2, SampleRate, bitsPerSample: 16))
                {
                    session.WriteInterleavedSamples(source, source.Length / 2);
                    session.Finish();
                }

                var (_, decoded) = Mp3TestDecoder.DecodeAll(mp3Path);

                LevelDifferenceDb(source, decoded, channels: 2).Should().BeApproximately(0, LevelToleranceDb);
            }
            finally
            {
                File.Delete(mp3Path);
            }
        }

        [Fact]
        public void Encode_8BitWav_Should_DecodeAtTheSourceLevel()
        {
            var signedSamples = BuildStereoSine(amplitude: 64, SampleRate * 2);
            var wavPath = TempPath("level8bit", ".wav");
            var mp3Path = TempPath("level8bit");
            WavFileBuilder.Create(wavPath, channels: 2, SampleRate, bitsPerSample: 8, [.. signedSamples.Select(sample => sample + 128)]);

            try
            {
                Mp3Encoder.Encode(wavPath, mp3Path);

                var (_, decoded) = Mp3TestDecoder.DecodeAll(mp3Path);
                var sourceAt16Bit = signedSamples.Select(sample => sample << 8).ToArray();

                LevelDifferenceDb(sourceAt16Bit, decoded, channels: 2).Should().BeApproximately(0, LevelToleranceDb);
            }
            finally
            {
                File.Delete(wavPath);
                File.Delete(mp3Path);
            }
        }

        [Theory]
        [InlineData(-0.1)]
        [InlineData(0.0)]
        public void Decode_SineToneNearFullScale_Should_ClampInsteadOfWrappingAround(double levelDbfs)
        {
            var amplitude = 32767 * Math.Pow(10, levelDbfs / 20);
            var source = BuildStereoSine(amplitude, SampleRate);
            var mp3Path = TempPath("loud");

            try
            {
                using (var session = Mp3Encoder.OpenSession(mp3Path, channels: 2, SampleRate, bitsPerSample: 16))
                {
                    session.WriteInterleavedSamples(source, source.Length / 2);
                    session.Finish();
                }

                var (_, decoded) = Mp3TestDecoder.DecodeAll(mp3Path);

                LargestStepBetweenNeighbors(decoded, channels: 2).Should().BeLessThan(10000);
                LevelDifferenceDb(source, decoded, channels: 2).Should().BeApproximately(0, LevelToleranceDb);
            }
            finally
            {
                File.Delete(mp3Path);
            }
        }

        internal static int[] BuildStereoSine(double amplitude, int frameCount, int sampleRate = SampleRate)
        {
            var samples = new int[frameCount * 2];
            for (var i = 0; i < frameCount; i++)
            {
                var value = (int)Math.Round(amplitude * Math.Sin(2 * Math.PI * ToneFrequency * i / sampleRate));
                samples[i * 2] = value;
                samples[(i * 2) + 1] = value;
            }

            return samples;
        }

        internal static double LevelDifferenceDb(int[] source, int[] decoded, int channels, int sourceSampleRate = SampleRate, int decodedSampleRate = SampleRate)
        {
            var sourceRms = SteadyStateRms(source, channels, sourceSampleRate);
            var decodedRms = SteadyStateRms(decoded, channels, decodedSampleRate);

            return 20 * Math.Log10(decodedRms / sourceRms);
        }

        internal static int LargestStepBetweenNeighbors(int[] interleaved, int channels)
        {
            var largestStep = 0;
            for (var i = channels; i < interleaved.Length; i++)
            {
                largestStep = Math.Max(largestStep, Math.Abs(interleaved[i] - interleaved[i - channels]));
            }

            return largestStep;
        }

        internal static string TempPath(string name, string extension = ".mp3")
            => Path.Combine(Path.GetTempPath(), $"mp3_{name}_{Guid.NewGuid():N}{extension}");

        private static double SteadyStateRms(int[] interleaved, int channels, int sampleRate)
        {
            var warmUpFrames = sampleRate / 10;
            var wholePeriodFrames = (int)(sampleRate / ToneFrequency * 500);
            var sumOfSquares = 0.0;

            for (var frame = warmUpFrames; frame < warmUpFrames + wholePeriodFrames; frame++)
            {
                var sample = (double)interleaved[frame * channels];
                sumOfSquares += sample * sample;
            }

            return Math.Sqrt(sumOfSquares / wholePeriodFrames);
        }
    }
}
