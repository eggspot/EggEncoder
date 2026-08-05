using BenchmarkDotNet.Attributes;
using EggEncoder.Codecs.Aac;

namespace EggEncoder.Benchmarks
{
    // The AAC session is the encoder most directly used in a real-time/streaming pipeline --
    // this measures per-sample push throughput and allocation behavior of that hot path.
    [MemoryDiagnoser]
    public class AacStreamingBenchmarks
    {
        private const int SampleRate = 44100;
        private const int TotalSamples = SampleRate * 5;

        private int[] _samples = null!;

        [GlobalSetup]
        public void Setup()
        {
            _samples = new int[TotalSamples];
            for (var i = 0; i < TotalSamples; i++)
            {
                _samples[i] = (short)(10000 * Math.Sin(2 * Math.PI * 440 * i / SampleRate));
            }
        }

        [Benchmark(Description = "AacEncoderSession: stream 5s mono PCM incrementally")]
        public void StreamFiveSecondsMono()
        {
            var path = Path.Combine(Path.GetTempPath(), $"bench_aac_{Guid.NewGuid():N}.aac");
            try
            {
                using var session = AacEncoderSession.OpenSession(path, channels: 1, SampleRate);

                const int block = 1024;
                for (var offset = 0; offset < _samples.Length; offset += block)
                {
                    var count = Math.Min(block, _samples.Length - offset);
                    session.WriteInterleavedSamples(_samples[offset..(offset + count)], count);
                }

                session.Finish();
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
