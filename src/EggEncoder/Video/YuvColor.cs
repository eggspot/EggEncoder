namespace EggEncoder.Video;

/// <summary>An 8-bit Y'CbCr colour, as stored in a <see cref="VideoFrame"/>.</summary>
/// <param name="Y">Luma.</param>
/// <param name="U">Blue-difference chroma (Cb), 128 = none.</param>
/// <param name="V">Red-difference chroma (Cr), 128 = none.</param>
public readonly record struct YuvColor(byte Y, byte U, byte V)
{
    /// <summary>The Y'CbCr value of an 8-bit R'G'B' colour, rounded to the nearest value and clamped.</summary>
    public static YuvColor FromRgb(byte r, byte g, byte b, VideoColorMatrix matrix, VideoColorRange range)
    {
        var (kr, kb) = Coefficients(matrix);
        var red = r / 255.0;
        var green = g / 255.0;
        var blue = b / 255.0;
        var luma = (kr * red) + ((1 - kr - kb) * green) + (kb * blue);
        var cb = (blue - luma) / (2 * (1 - kb));
        var cr = (red - luma) / (2 * (1 - kr));
        return range == VideoColorRange.Limited
            ? new YuvColor(ToByte(16 + (219 * luma)), ToByte(128 + (224 * cb)), ToByte(128 + (224 * cr)))
            : new YuvColor(ToByte(255 * luma), ToByte(128 + (255 * cb)), ToByte(128 + (255 * cr)));
    }

    /// <summary>The 8-bit R'G'B' value of this colour, rounded to the nearest value and clamped.</summary>
    public (byte R, byte G, byte B) ToRgb(VideoColorMatrix matrix, VideoColorRange range)
    {
        var (kr, kb) = Coefficients(matrix);
        var (luma, cb, cr) = range == VideoColorRange.Limited
            ? ((Y - 16) / 219.0, (U - 128) / 224.0, (V - 128) / 224.0)
            : (Y / 255.0, (U - 128) / 255.0, (V - 128) / 255.0);
        var red = luma + (2 * (1 - kr) * cr);
        var blue = luma + (2 * (1 - kb) * cb);
        var green = (luma - (kr * red) - (kb * blue)) / (1 - kr - kb);
        return (ToByte(255 * red), ToByte(255 * green), ToByte(255 * blue));
    }

    internal static double[] ConversionMatrix(VideoColorMatrix from, VideoColorMatrix to)
    {
        var result = new double[9];
        var (kr, kb) = Coefficients(from);
        var (krOut, kbOut) = Coefficients(to);
        for (var column = 0; column < 3; column++)
        {
            var luma = column == 0 ? 1.0 : 0;
            var cb = column == 1 ? 1.0 : 0;
            var cr = column == 2 ? 1.0 : 0;
            var red = luma + (2 * (1 - kr) * cr);
            var blue = luma + (2 * (1 - kb) * cb);
            var green = (luma - (kr * red) - (kb * blue)) / (1 - kr - kb);
            var outLuma = (krOut * red) + ((1 - krOut - kbOut) * green) + (kbOut * blue);
            result[column] = outLuma;
            result[3 + column] = (blue - outLuma) / (2 * (1 - kbOut));
            result[6 + column] = (red - outLuma) / (2 * (1 - krOut));
        }

        return result;
    }

    internal static (double Kr, double Kb) Coefficients(VideoColorMatrix matrix) => matrix switch
    {
        VideoColorMatrix.Bt601 => (0.299, 0.114),
        VideoColorMatrix.Bt709 => (0.2126, 0.0722),
        _ => throw new ArgumentOutOfRangeException(nameof(matrix), matrix, "Unknown colour matrix")
    };

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
}

/// <summary>An 8-bit R'G'B' colour with straight (not premultiplied) alpha; 255 is opaque.</summary>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
/// <param name="A">Alpha.</param>
public readonly record struct RgbaColor(byte R, byte G, byte B, byte A = 255)
{
    /// <summary>The colour in a frame's Y'CbCr, ignoring alpha.</summary>
    public YuvColor ToYuv(VideoColorMatrix matrix, VideoColorRange range) => YuvColor.FromRgb(R, G, B, matrix, range);
}
