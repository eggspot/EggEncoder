using BenchmarkDotNet.Attributes;
using EggEncoder.Codecs.H264;

namespace EggEncoder.Benchmarks
{
    [MemoryDiagnoser]
    public class H264DecodeBenchmarks
    {
        private readonly List<byte[]> _accessUnits = new();

        [GlobalSetup]
        public void Setup()
        {
            var path = Environment.GetEnvironmentVariable("EGGENCODER_H264_BENCHMARK_FILE");
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                throw new InvalidOperationException("Set EGGENCODER_H264_BENCHMARK_FILE to an Annex B .h264 file to run H264DecodeBenchmarks");
            }

            SplitAccessUnits(File.ReadAllBytes(path));
        }

        [Benchmark(Description = "H264Decoder: whole Annex B file, one picture at a time")]
        public int Decode() => DecodeAll(1);

        [Benchmark(Description = "H264Decoder: whole Annex B file, default frame threads")]
        public int DecodeFrameThreads() => DecodeAll(0);

        private int DecodeAll(int maxDegreeOfParallelism)
        {
            var frames = 0;
            using var decoder = new H264Decoder(null, maxDegreeOfParallelism);
            foreach (var unit in _accessUnits)
            {
                decoder.Decode(unit, TimeSpan.Zero, frame =>
                {
                    frames++;
                    frame.Dispose();
                });
            }

            decoder.Flush(frame =>
            {
                frames++;
                frame.Dispose();
            });
            return frames;
        }

        private void SplitAccessUnits(byte[] data)
        {
            var starts = new List<int>();
            for (var i = 0; i + 2 < data.Length; i++)
            {
                if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
                {
                    starts.Add(i + 3);
                    i += 2;
                }
            }

            List<byte>? current = null;
            var hasSlice = false;
            for (var k = 0; k < starts.Count; k++)
            {
                var end = k + 1 < starts.Count ? starts[k + 1] - 3 : data.Length;
                while (end > starts[k] && data[end - 1] == 0)
                {
                    end--;
                }

                var type = data[starts[k]] & 0x1F;
                var isSlice = type is 1 or 5;
                var firstSlice = isSlice && end > starts[k] + 1 && (data[starts[k] + 1] & 0x80) != 0;
                if (current == null || (hasSlice && (firstSlice || type is 6 or 7 or 8 or 9)))
                {
                    if (current != null)
                    {
                        _accessUnits.Add(current.ToArray());
                    }

                    current = new List<byte>();
                    hasSlice = false;
                }

                current.AddRange([0, 0, 0, 1]);
                current.AddRange(data.AsSpan(starts[k], end - starts[k]).ToArray());
                hasSlice |= isSlice;
            }

            if (current != null)
            {
                _accessUnits.Add(current.ToArray());
            }
        }
    }
}
