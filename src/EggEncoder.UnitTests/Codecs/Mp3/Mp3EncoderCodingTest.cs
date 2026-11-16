using EggEncoder.Codecs.Mp3;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Mp3
{
    public class Mp3EncoderCodingTest
    {
        private const int SampleRate = 44100;
        private const int Channels = 2;
        private const int DecodedLag = 1057;

        [Theory]
        [InlineData(192, 22)]
        [InlineData(320, 31.5)]
        public void Noisy_Music_At_A_Fixed_Bitrate_Should_Meet_An_Snr_Floor(int bitRateKbps, double minimumSnrDb)
        {
            var samples = NoisyMusic(seconds: 3);

            var path = Encode(samples, encoder => Mp3Encoder.OpenSession(encoder, Channels, SampleRate, bitsPerSample: 16, bitRateKbps: bitRateKbps));

            try
            {
                Snr(samples, path).Should().BeGreaterThan(minimumSnrDb);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Hard_Content_At_The_Best_Vbr_Quality_Should_Get_A_High_Bitrate()
        {
            var samples = NoisyMusic(seconds: 3);

            var path = Encode(samples, encoder => Mp3Encoder.OpenVbrSession(encoder, Channels, SampleRate, bitsPerSample: 16, quality: 0));

            try
            {
                var kilobitsPerSecond = new FileInfo(path).Length * 8.0 / 3 / 1000;
                kilobitsPerSecond.Should().BeGreaterThan(250);
                Snr(samples, path).Should().BeGreaterThan(30);
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static string Encode(int[] samples, Func<string, Mp3EncoderSession> open)
        {
            var path = Path.Combine(Path.GetTempPath(), $"mp3_coding_{Guid.NewGuid():N}.mp3");
            using var session = open(path);
            session.WriteInterleavedSamples(samples, samples.Length / Channels);
            session.Finish();
            return path;
        }

        private static double Snr(int[] samples, string path)
        {
            var (_, decoded) = Mp3TestDecoder.DecodeAll(path);
            double signal = 0;
            double noise = 0;
            var frames = samples.Length / Channels;
            for (var frame = SampleRate / 10; frame < frames - (SampleRate / 10); frame++)
            {
                for (var channel = 0; channel < Channels; channel++)
                {
                    var expected = samples[(frame * Channels) + channel];
                    var actual = decoded[((frame + DecodedLag) * Channels) + channel];
                    signal += (double)expected * expected;
                    noise += (double)(actual - expected) * (actual - expected);
                }
            }

            return 10 * Math.Log10(signal / Math.Max(noise, 1));
        }

        private static int[] NoisyMusic(int seconds)
        {
            var random = new Random(23);
            var frames = seconds * SampleRate;
            var samples = new int[frames * Channels];
            var low = 0.0;
            for (var frame = 0; frame < frames; frame++)
            {
                var t = frame / (double)SampleRate;
                low = (0.97 * low) + (0.03 * (random.NextDouble() - 0.5));
                var noise = (random.NextDouble() - 0.5) * 1500;
                var tone = (7000 * Math.Sin(2 * Math.PI * 220 * t)) + (2500 * Math.Sin(2 * Math.PI * 660 * t));
                samples[frame * Channels] = (int)(tone + noise + (60000 * low));
                samples[(frame * Channels) + 1] = (int)((0.8 * tone) + noise + (50000 * low));
            }

            return samples;
        }
    }
}
