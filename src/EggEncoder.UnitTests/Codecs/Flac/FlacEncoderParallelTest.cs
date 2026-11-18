using EggEncoder.Codecs.Flac;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Flac
{
    public class FlacEncoderParallelTest
    {
        [Theory]
        [InlineData(16, 44100)]
        [InlineData(24, 48000)]
        public void Output_Should_Not_Depend_On_The_Thread_Count(int bitsPerSample, int sampleRate)
        {
            var samples = Signal(bitsPerSample, frames: (4096 * 9) + 321);

            var oneThread = Encode(samples, bitsPerSample, sampleRate, threads: 1, writeFrames: samples.Length / 2);
            var fourThreads = Encode(samples, bitsPerSample, sampleRate, threads: 4, writeFrames: samples.Length / 2);
            var fourThreadsSmallWrites = Encode(samples, bitsPerSample, sampleRate, threads: 4, writeFrames: 1000);

            fourThreads.Should().Equal(oneThread);
            fourThreadsSmallWrites.Should().Equal(oneThread);
        }

        private static byte[] Encode(int[] samples, int bitsPerSample, int sampleRate, int threads, int writeFrames)
        {
            using var scope = CodecParallelism.Limit(threads);
            using var stream = new MemoryStream();
            using (var session = FlacEncoder.OpenSession(stream, 2, bitsPerSample, sampleRate))
            {
                var block = new int[writeFrames * 2];
                for (var offset = 0; offset < samples.Length; offset += block.Length)
                {
                    var count = Math.Min(block.Length, samples.Length - offset);
                    Array.Copy(samples, offset, block, 0, count);
                    session.WriteInterleavedSamples(block, count / 2);
                }

                session.Finish();
            }

            return stream.ToArray();
        }

        private static int[] Signal(int bitsPerSample, int frames)
        {
            var random = new Random(5);
            var amplitude = (1 << (bitsPerSample - 2)) - 1;
            var samples = new int[frames * 2];
            for (var i = 0; i < frames; i++)
            {
                samples[i * 2] = (int)((Math.Sin(2 * Math.PI * 330 * i / 44100.0) * 0.5 * amplitude) + ((random.NextDouble() - 0.5) * 0.1 * amplitude));
                samples[(i * 2) + 1] = (int)((Math.Sin(2 * Math.PI * 495 * i / 44100.0) * 0.4 * amplitude) + ((random.NextDouble() - 0.5) * 0.1 * amplitude));
            }

            return samples;
        }
    }
}
