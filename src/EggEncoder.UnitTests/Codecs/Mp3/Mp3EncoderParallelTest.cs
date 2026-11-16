using EggEncoder.Codecs.Mp3;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Mp3
{
    public class Mp3EncoderParallelTest
    {
        private const int SampleRate = 44100;
        private const int Channels = 2;

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Output_Should_Not_Depend_On_Threads_Or_How_Samples_Arrive(bool variableBitRate)
        {
            var samples = Music(7.3);

            var oneThreadOneWrite = Encode(samples, variableBitRate, threads: 1, writeFrames: samples.Length / Channels);
            var fourThreadsOneWrite = Encode(samples, variableBitRate, threads: 4, writeFrames: samples.Length / Channels);
            var twoThreadsSmallWrites = Encode(samples, variableBitRate, threads: 2, writeFrames: 1001);

            fourThreadsOneWrite.Should().Equal(oneThreadOneWrite);
            twoThreadsSmallWrites.Should().Equal(oneThreadOneWrite);
        }

        private static byte[] Encode(int[] samples, bool variableBitRate, int threads, int writeFrames)
        {
            var path = Path.Combine(Path.GetTempPath(), $"mp3_parallel_{Guid.NewGuid():N}.mp3");
            try
            {
                using (var session = variableBitRate
                    ? Mp3Encoder.OpenVbrSession(path, Channels, SampleRate, bitsPerSample: 16, quality: 2)
                    : Mp3Encoder.OpenSession(path, Channels, SampleRate, bitsPerSample: 16, bitRateKbps: 192))
                {
                    session.MaxDegreeOfParallelism = threads;
                    var block = new int[writeFrames * Channels];
                    for (var offset = 0; offset < samples.Length; offset += block.Length)
                    {
                        var count = Math.Min(block.Length, samples.Length - offset);
                        Array.Copy(samples, offset, block, 0, count);
                        session.WriteInterleavedSamples(block, count / Channels);
                    }

                    session.Finish();
                }

                return File.ReadAllBytes(path);
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static int[] Music(double seconds)
        {
            var random = new Random(17);
            var frames = (int)(seconds * SampleRate);
            var samples = new int[frames * Channels];
            for (var frame = 0; frame < frames; frame++)
            {
                var t = frame / (double)SampleRate;
                var swell = 0.5 + (0.5 * Math.Sin(2 * Math.PI * 0.3 * t));
                samples[frame * Channels] = (int)((swell * 9000 * Math.Sin(2 * Math.PI * 330 * t)) + (800 * (random.NextDouble() - 0.5)));
                samples[(frame * Channels) + 1] = (int)((swell * 7000 * Math.Sin(2 * Math.PI * 495 * t)) + (1200 * (random.NextDouble() - 0.5)));
            }

            return samples;
        }
    }
}
