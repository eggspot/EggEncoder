using BenchmarkDotNet.Attributes;
using EggEncoder.Codecs.H264;
using EggEncoder.Video;

namespace EggEncoder.Benchmarks
{
    [MemoryDiagnoser]
    public class H264EncodeBenchmarks
    {
        private const int Width = 1080;
        private const int Height = 1920;
        private readonly List<VideoFrame> _frames = new();

        [Params(1, 4)]
        public int Threads { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            var path = Environment.GetEnvironmentVariable("EGGENCODER_H264_ENCODE_BENCHMARK_FILE");
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                LoadRaw(File.ReadAllBytes(path));
                return;
            }

            for (var index = 0; index < 30; index++)
            {
                var frame = VideoFrame.Create(Width, Height);
                for (var y = 0; y < Height; y++)
                {
                    var row = frame.GetRow(VideoPlane.Y, y);
                    for (var x = 0; x < Width; x++)
                    {
                        row[x] = (byte)(((x + (index * 3)) * 7 ^ (y * 5)) & 0xFF);
                    }
                }

                frame.GetRow(VideoPlane.U, 0).Fill(128);
                _frames.Add(frame);
            }
        }

        [GlobalCleanup]
        public void Cleanup() => _frames.ForEach(frame => frame.Dispose());

        [Benchmark(Description = "H264Encoder: 1080x1920 High profile CRF 23, frames per call = loaded frames")]
        public long Encode()
        {
            long bytes = 0;
            using var encoder = new H264Encoder(new H264EncoderSettings
            {
                Width = Width,
                Height = Height,
                FrameRate = new VideoFrameRate(30),
                ConstantRateFactor = 23,
                Profile = H264Profile.High,
                MaxDegreeOfParallelism = Threads
            });
            foreach (var frame in _frames)
            {
                encoder.Encode(frame, encoded => bytes += encoded.Payload.Length);
            }

            return bytes;
        }

        private void LoadRaw(byte[] data)
        {
            var frameBytes = Width * Height * 3 / 2;
            for (var offset = 0; offset + frameBytes <= data.Length; offset += frameBytes)
            {
                var frame = VideoFrame.Create(Width, Height);
                var position = offset;
                for (var y = 0; y < Height; y++, position += Width)
                {
                    data.AsSpan(position, Width).CopyTo(frame.GetRow(VideoPlane.Y, y));
                }

                foreach (var plane in new[] { VideoPlane.U, VideoPlane.V })
                {
                    for (var y = 0; y < Height / 2; y++, position += Width / 2)
                    {
                        data.AsSpan(position, Width / 2).CopyTo(frame.GetRow(plane, y));
                    }
                }

                frame.Timestamp = TimeSpan.FromSeconds(_frames.Count / 30.0);
                _frames.Add(frame);
            }
        }
    }
}
