using EggEncoder.Video;

namespace EggEncoder.UnitTests.Video.Filters
{
    internal static class FrameTestSupport
    {
        public static VideoFrame Load(string fileName, int width, int height)
        {
            var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Video", "Filters", fileName));
            var frame = VideoFrame.Create(width, height);
            var offset = 0;
            offset = Read(frame, VideoPlane.Y, height, bytes, offset);
            offset = Read(frame, VideoPlane.U, frame.ChromaHeight, bytes, offset);
            Read(frame, VideoPlane.V, frame.ChromaHeight, bytes, offset);
            return frame;
        }

        public static VideoFrame Input() => Load("filter_input_64x48.yuv", 64, 48);

        public static VideoFrame Pattern(int width, int height, Func<VideoPlane, int, int, byte> value)
        {
            var frame = VideoFrame.Create(width, height);
            foreach (var plane in new[] { VideoPlane.Y, VideoPlane.U, VideoPlane.V })
            {
                var rows = plane == VideoPlane.Y ? height : frame.ChromaHeight;
                for (var y = 0; y < rows; y++)
                {
                    var row = frame.GetRow(plane, y);
                    for (var x = 0; x < row.Length; x++)
                    {
                        row[x] = value(plane, x, y);
                    }
                }
            }

            return frame;
        }

        public static VideoFrame Solid(int width, int height, YuvColor color)
        {
            var frame = VideoFrame.Create(width, height);
            frame.Fill(color);
            return frame;
        }

        public static double Psnr(VideoFrame actual, VideoFrame expected, VideoPlane plane)
        {
            var rows = plane == VideoPlane.Y ? expected.Height : expected.ChromaHeight;
            var squared = 0.0;
            var count = 0;
            for (var y = 0; y < rows; y++)
            {
                var a = actual.GetRow(plane, y);
                var e = expected.GetRow(plane, y);
                for (var x = 0; x < e.Length; x++)
                {
                    squared += (a[x] - e[x]) * (a[x] - e[x]);
                    count++;
                }
            }

            return squared == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / (squared / count));
        }

        public static bool SamePixels(VideoFrame a, VideoFrame b) =>
            a.Width == b.Width && a.Height == b.Height
            && double.IsPositiveInfinity(Psnr(a, b, VideoPlane.Y))
            && double.IsPositiveInfinity(Psnr(a, b, VideoPlane.U))
            && double.IsPositiveInfinity(Psnr(a, b, VideoPlane.V));

        public static VideoFrame RunOne(IVideoFrameTransform transform, VideoFrame frame)
        {
            var output = new List<VideoFrame>();
            transform.Apply(frame, output.Add);
            if (output.Count != 1)
            {
                throw new InvalidOperationException($"Expected one frame, got {output.Count}");
            }

            return output[0];
        }

        private static int Read(VideoFrame frame, VideoPlane plane, int rows, byte[] bytes, int offset)
        {
            for (var y = 0; y < rows; y++)
            {
                var row = frame.GetRow(plane, y);
                bytes.AsSpan(offset, row.Length).CopyTo(row);
                offset += row.Length;
            }

            return offset;
        }
    }
}
