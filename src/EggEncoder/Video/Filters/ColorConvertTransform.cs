namespace EggEncoder.Video.Filters;

/// <summary>
/// Converts frames to another Y'CbCr range and matrix (ffmpeg <c>scale=out_range:out_color_matrix</c>):
/// each sample goes through R'G'B' without clamping in between, so in-gamut colours keep their look.
/// Each output chroma sample uses the mean of the luma samples it covers. A frame already in the
/// target range and matrix passes through untouched.
/// </summary>
public sealed class ColorConvertTransform : IVideoFrameTransform
{
    private readonly VideoColorRange _range;
    private readonly VideoColorMatrix _matrix;

    /// <param name="range">The range to convert to.</param>
    /// <param name="matrix">The matrix to convert to.</param>
    /// <exception cref="ArgumentOutOfRangeException">The range or matrix is unknown.</exception>
    public ColorConvertTransform(VideoColorRange range, VideoColorMatrix matrix)
    {
        if (!Enum.IsDefined(range))
        {
            throw new ArgumentOutOfRangeException(nameof(range), range, "Unknown range");
        }

        if (!Enum.IsDefined(matrix))
        {
            throw new ArgumentOutOfRangeException(nameof(matrix), matrix, "Unknown matrix");
        }

        _range = range;
        _matrix = matrix;
    }

    /// <inheritdoc />
    public (int Width, int Height) GetOutputSize(int inputWidth, int inputHeight) => (inputWidth, inputHeight);

    /// <inheritdoc />
    public bool CanChangeFrameCount => false;

    /// <inheritdoc />
    public void Apply(VideoFrame frame, Action<VideoFrame> emit)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(emit);
        if (frame.Range == _range && frame.Matrix == _matrix)
        {
            emit(frame);
            return;
        }

        FrameTransformSupport.ApplyOwning(frame, emit, Convert);
    }

    private VideoFrame Convert(VideoFrame source)
    {
        var output = source.Pool.Rent(source.Width, source.Height, _range, _matrix);
        output.Timestamp = source.Timestamp;
        output.Duration = source.Duration;
        var matrix = YuvColor.ConversionMatrix(source.Matrix, _matrix);
        var (lumaIn, lumaInOffset, chromaIn) = Scales(source.Range);
        var (lumaOut, lumaOutOffset, chromaOut) = Scales(_range);
        var lumaTable = new double[256];
        var chromaTable = new double[256];
        for (var i = 0; i < 256; i++)
        {
            lumaTable[i] = (i - lumaInOffset) / lumaIn;
            chromaTable[i] = (i - 128) / chromaIn;
        }

        for (var y = 0; y < source.Height; y++)
        {
            var luma = source.GetRow(VideoPlane.Y, y);
            var cb = source.GetRow(VideoPlane.U, y / 2);
            var cr = source.GetRow(VideoPlane.V, y / 2);
            var target = output.GetRow(VideoPlane.Y, y);
            for (var x = 0; x < source.Width; x++)
            {
                var value = (matrix[0] * lumaTable[luma[x]]) + (matrix[1] * chromaTable[cb[x / 2]]) + (matrix[2] * chromaTable[cr[x / 2]]);
                target[x] = ToByte(lumaOutOffset + (lumaOut * value));
            }
        }

        for (var y = 0; y < source.ChromaHeight; y++)
        {
            var cb = source.GetRow(VideoPlane.U, y);
            var cr = source.GetRow(VideoPlane.V, y);
            var targetCb = output.GetRow(VideoPlane.U, y);
            var targetCr = output.GetRow(VideoPlane.V, y);
            for (var x = 0; x < source.ChromaWidth; x++)
            {
                var l = lumaTable[AverageLuma(source, x, y)];
                var b = chromaTable[cb[x]];
                var r = chromaTable[cr[x]];
                targetCb[x] = ToByte(128 + (chromaOut * ((matrix[3] * l) + (matrix[4] * b) + (matrix[5] * r))));
                targetCr[x] = ToByte(128 + (chromaOut * ((matrix[6] * l) + (matrix[7] * b) + (matrix[8] * r))));
            }
        }

        return output;
    }

    private static int AverageLuma(VideoFrame frame, int chromaX, int chromaY)
    {
        var sum = 0;
        var count = 0;
        for (var y = chromaY * 2; y < Math.Min((chromaY * 2) + 2, frame.Height); y++)
        {
            var row = frame.GetRow(VideoPlane.Y, y);
            for (var x = chromaX * 2; x < Math.Min((chromaX * 2) + 2, frame.Width); x++)
            {
                sum += row[x];
                count++;
            }
        }

        return (sum + (count / 2)) / count;
    }

    private static (double Luma, int LumaOffset, double Chroma) Scales(VideoColorRange range) =>
        range == VideoColorRange.Limited ? (219, 16, 224) : (255, 0, 255);

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
}
