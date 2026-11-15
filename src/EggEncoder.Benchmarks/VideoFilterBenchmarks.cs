using BenchmarkDotNet.Attributes;
using EggEncoder.Video;
using EggEncoder.Video.Filters;

namespace EggEncoder.Benchmarks
{
    [MemoryDiagnoser]
    public class VideoFilterBenchmarks
    {
        private VideoFrame _landscape = null!;
        private VideoFrame _portrait = null!;
        private FitTransform _cover = null!;
        private BoxBlurTransform _blur = null!;
        private OverlayTransform _overlay = null!;

        [GlobalSetup]
        public void Setup()
        {
            _landscape = Pattern(1920, 1080);
            _portrait = Pattern(1080, 1920);
            _cover = new FitTransform(1080, 1920, FitMode.Cover);
            _blur = new BoxBlurTransform(20, 10);
            var logo = new RgbaImage(400, 200);
            logo.Pixels.Fill(200);
            _overlay = new OverlayTransform(logo, 100, 1500);
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _landscape.Dispose();
            _portrait.Dispose();
        }

        [Benchmark(Description = "FitTransform Cover bicubic: 1920x1080 -> 1080x1920, one thread")]
        public void Cover() => _cover.Apply(_landscape.Clone(), f => f.Dispose());

        [Benchmark(Description = "BoxBlurTransform radius 20/10: 1080x1920, one thread")]
        public void Blur() => _blur.Apply(_portrait.Clone(), f => f.Dispose());

        [Benchmark(Description = "OverlayTransform 400x200 RGBA logo on 1080x1920")]
        public void Overlay() => _overlay.Apply(_portrait.Clone(), f => f.Dispose());

        private static VideoFrame Pattern(int width, int height)
        {
            var frame = VideoFrame.Create(width, height);
            for (var y = 0; y < height; y++)
            {
                var row = frame.GetRow(VideoPlane.Y, y);
                for (var x = 0; x < width; x++)
                {
                    row[x] = (byte)((x * 7) + (y * 3));
                }
            }

            frame.PlaneU.Fill(100);
            frame.PlaneV.Fill(150);
            return frame;
        }
    }
}
