namespace EggEncoder.Video.Filters;

/// <summary>
/// Draws onto frames with alpha blending. Luma is blended per pixel; each chroma sample is blended with the average
/// coverage of the luma pixels it belongs to (those inside the frame), so shapes at odd positions blend exactly.
/// Colours are converted with the frame's own matrix and range, and anything outside the frame is clipped.
/// </summary>
public static class VideoDraw
{
    /// <summary>Fills a rectangle with a colour, blended by the colour's alpha.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
    public static void FillRectangle(VideoFrame frame, VideoRectangle rectangle, RgbaColor color)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (color.A == 0 || rectangle.Width <= 0 || rectangle.Height <= 0)
        {
            return;
        }

        Composite(frame, rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height, new SolidLayer(color.ToYuv(frame.Matrix, frame.Range), color.A));
    }

    /// <summary>Draws a coverage mask in one colour with its top-left corner at (<paramref name="x"/>, <paramref name="y"/>); coverage × the colour's alpha is the blend.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static void BlendMask(VideoFrame frame, AlphaMask mask, int x, int y, RgbaColor color)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(mask);
        if (color.A == 0 || mask.Width == 0 || mask.Height == 0)
        {
            return;
        }

        Composite(frame, x, y, mask.Width, mask.Height, new MaskLayer(mask, color.ToYuv(frame.Matrix, frame.Range), color.A));
    }

    internal static void Composite<TLayer>(VideoFrame frame, int x, int y, int width, int height, TLayer layer)
        where TLayer : struct, ILayer
    {
        var visible = new VideoRectangle(x, y, width, height).ClipTo(frame.Width, frame.Height);
        if (visible.Width == 0)
        {
            return;
        }

        for (var row = visible.Y; row < visible.Bottom; row++)
        {
            var output = frame.GetRow(VideoPlane.Y, row);
            var layerRow = (row - y) * width;
            for (var column = visible.X; column < visible.Right; column++)
            {
                var index = layerRow + column - x;
                int alpha = layer.Alpha(index);
                if (alpha != 0)
                {
                    output[column] = (byte)(((output[column] * (255 - alpha)) + (layer.Y(index) * alpha) + 127) / 255);
                }
            }
        }

        var chromaTop = visible.Y / 2;
        var chromaBottom = (visible.Bottom + 1) / 2;
        var chromaLeft = visible.X / 2;
        var chromaRight = (visible.Right + 1) / 2;
        for (var chromaRow = chromaTop; chromaRow < chromaBottom; chromaRow++)
        {
            var outputU = frame.GetRow(VideoPlane.U, chromaRow);
            var outputV = frame.GetRow(VideoPlane.V, chromaRow);
            var lumaRows = Math.Min(2, frame.Height - (2 * chromaRow));
            for (var chromaColumn = chromaLeft; chromaColumn < chromaRight; chromaColumn++)
            {
                var lumaColumns = Math.Min(2, frame.Width - (2 * chromaColumn));
                var alphaSum = 0;
                var uSum = 0;
                var vSum = 0;
                for (var dy = 0; dy < lumaRows; dy++)
                {
                    var layerY = (2 * chromaRow) + dy - y;
                    if ((uint)layerY >= (uint)height)
                    {
                        continue;
                    }

                    for (var dx = 0; dx < lumaColumns; dx++)
                    {
                        var layerX = (2 * chromaColumn) + dx - x;
                        if ((uint)layerX >= (uint)width)
                        {
                            continue;
                        }

                        var index = (layerY * width) + layerX;
                        int alpha = layer.Alpha(index);
                        alphaSum += alpha;
                        uSum += layer.U(index) * alpha;
                        vSum += layer.V(index) * alpha;
                    }
                }

                if (alphaSum == 0)
                {
                    continue;
                }

                var cellWeight = 255 * lumaRows * lumaColumns;
                outputU[chromaColumn] = (byte)(((outputU[chromaColumn] * (cellWeight - alphaSum)) + uSum + (cellWeight / 2)) / cellWeight);
                outputV[chromaColumn] = (byte)(((outputV[chromaColumn] * (cellWeight - alphaSum)) + vSum + (cellWeight / 2)) / cellWeight);
            }
        }
    }

    internal interface ILayer
    {
        byte Alpha(int index);

        byte Y(int index);

        byte U(int index);

        byte V(int index);
    }

    internal readonly struct SolidLayer(YuvColor color, byte alpha) : ILayer
    {
        public byte Alpha(int index) => alpha;

        public byte Y(int index) => color.Y;

        public byte U(int index) => color.U;

        public byte V(int index) => color.V;
    }

    internal readonly struct MaskLayer(AlphaMask mask, YuvColor color, byte alpha) : ILayer
    {
        private readonly byte[] _coverage = mask.CoverageArray;

        public byte Alpha(int index) => (byte)(((_coverage[index] * alpha) + 127) / 255);

        public byte Y(int index) => color.Y;

        public byte U(int index) => color.U;

        public byte V(int index) => color.V;
    }

    internal readonly struct ImageLayer(byte[] y, byte[] u, byte[] v, byte[] alpha) : ILayer
    {
        public byte Alpha(int index) => alpha[index];

        public byte Y(int index) => y[index];

        public byte U(int index) => u[index];

        public byte V(int index) => v[index];
    }
}
