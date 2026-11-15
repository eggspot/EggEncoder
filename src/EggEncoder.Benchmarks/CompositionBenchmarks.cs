using BenchmarkDotNet.Attributes;
using EggEncoder.Containers.Mp4;
using EggEncoder.Video;
using EggEncoder.Video.Composition;

namespace EggEncoder.Benchmarks
{
    [MemoryDiagnoser]
    public class CompositionBenchmarks
    {
        private static readonly TimeSpan Target = TimeSpan.FromSeconds(60);
        private VideoComposition _composition = null!;

        [GlobalSetup]
        public void Setup()
        {
            var clip = Environment.GetEnvironmentVariable("EGGENCODER_COMPOSITION_BENCHMARK_CLIP");
            if (string.IsNullOrEmpty(clip) || !File.Exists(clip))
            {
                throw new InvalidOperationException("Set EGGENCODER_COMPOSITION_BENCHMARK_CLIP to an H.264 MP4 to run CompositionBenchmarks");
            }

            using var reader = Mp4VideoReader.Open(clip);
            var length = reader.Duration;
            var scenes = new List<CompositionScene>();
            for (var covered = TimeSpan.Zero; covered < Target; covered += length)
            {
                var piece = Target - covered < length ? Target - covered : length;
                scenes.Add(new CompositionScene { Source = clip, Duration = piece });
            }

            _composition = new VideoComposition
            {
                Width = 1080,
                Height = 1920,
                FrameRate = new VideoFrameRate(30),
                Scenes = scenes,
                MaxDegreeOfParallelism = 4
            };
        }

        [Benchmark(Description = "VideoComposition: 60 s at 1080x1920 30 fps from one clip, 4 threads")]
        public int Render()
        {
            using var output = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
            return _composition.Render(output).FrameCount;
        }
    }
}
