using EggEncoder.Video;

namespace EggEncoder.Codecs.Jpeg;

internal sealed class JpegPlanes
{
    private JpegPlanes(int width, int height)
    {
        Width = width;
        Height = height;
        ChromaWidth = (width + 1) / 2;
        ChromaHeight = (height + 1) / 2;
        Luma = new byte[width * height];
        Cb = new byte[ChromaWidth * ChromaHeight];
        Cr = new byte[ChromaWidth * ChromaHeight];
    }

    public int Width { get; }

    public int Height { get; }

    public int ChromaWidth { get; }

    public int ChromaHeight { get; }

    public byte[] Luma { get; }

    public byte[] Cb { get; }

    public byte[] Cr { get; }

    public static JpegPlanes From(VideoFrame frame)
    {
        var planes = new JpegPlanes(frame.Width, frame.Height);
        if (frame.Range == VideoColorRange.Full && frame.Matrix == VideoColorMatrix.Bt601)
        {
            planes.CopyUnchanged(frame);
        }
        else
        {
            planes.Convert(frame);
        }

        return planes;
    }

    public void Load(byte[] plane, int width, int height, int x0, int y0, Span<float> samples)
    {
        for (var y = 0; y < 8; y++)
        {
            var row = Math.Min(y0 + y, height - 1) * width;
            for (var x = 0; x < 8; x++)
            {
                samples[(y * 8) + x] = plane[row + Math.Min(x0 + x, width - 1)] - 128f;
            }
        }
    }

    private void CopyUnchanged(VideoFrame frame)
    {
        for (var y = 0; y < Height; y++)
        {
            frame.GetRow(VideoPlane.Y, y)[..Width].CopyTo(Luma.AsSpan(y * Width, Width));
        }

        for (var y = 0; y < ChromaHeight; y++)
        {
            frame.GetRow(VideoPlane.U, y)[..ChromaWidth].CopyTo(Cb.AsSpan(y * ChromaWidth, ChromaWidth));
            frame.GetRow(VideoPlane.V, y)[..ChromaWidth].CopyTo(Cr.AsSpan(y * ChromaWidth, ChromaWidth));
        }
    }

    private void Convert(VideoFrame frame)
    {
        var matrix = YuvColor.ConversionMatrix(frame.Matrix, VideoColorMatrix.Bt601);
        var limited = frame.Range == VideoColorRange.Limited;
        var lumaScale = limited ? 1 / 219.0 : 1 / 255.0;
        var lumaOffset = limited ? 16 : 0;
        var chromaScale = limited ? 1 / 224.0 : 1 / 255.0;
        for (var y = 0; y < Height; y++)
        {
            var luma = frame.GetRow(VideoPlane.Y, y);
            var cb = frame.GetRow(VideoPlane.U, y / 2);
            var cr = frame.GetRow(VideoPlane.V, y / 2);
            for (var x = 0; x < Width; x++)
            {
                var l = (luma[x] - lumaOffset) * lumaScale;
                var b = (cb[x / 2] - 128) * chromaScale;
                var r = (cr[x / 2] - 128) * chromaScale;
                Luma[(y * Width) + x] = ToByte(255 * ((matrix[0] * l) + (matrix[1] * b) + (matrix[2] * r)));
            }
        }

        for (var y = 0; y < ChromaHeight; y++)
        {
            var cb = frame.GetRow(VideoPlane.U, y);
            var cr = frame.GetRow(VideoPlane.V, y);
            for (var x = 0; x < ChromaWidth; x++)
            {
                var l = (AverageLuma(frame, x, y) - lumaOffset) * lumaScale;
                var b = (cb[x] - 128) * chromaScale;
                var r = (cr[x] - 128) * chromaScale;
                Cb[(y * ChromaWidth) + x] = ToByte(128 + (255 * ((matrix[3] * l) + (matrix[4] * b) + (matrix[5] * r))));
                Cr[(y * ChromaWidth) + x] = ToByte(128 + (255 * ((matrix[6] * l) + (matrix[7] * b) + (matrix[8] * r))));
            }
        }
    }

    private double AverageLuma(VideoFrame frame, int chromaX, int chromaY)
    {
        var sum = 0;
        var count = 0;
        for (var y = chromaY * 2; y < Math.Min((chromaY * 2) + 2, Height); y++)
        {
            var row = frame.GetRow(VideoPlane.Y, y);
            for (var x = chromaX * 2; x < Math.Min((chromaX * 2) + 2, Width); x++)
            {
                sum += row[x];
                count++;
            }
        }

        return sum / (double)count;
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
}
