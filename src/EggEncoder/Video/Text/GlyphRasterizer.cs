namespace EggEncoder.Video.Text;

internal static class GlyphRasterizer
{
    private const double FlatnessTolerance = 0.025;

    public static (AlphaMask Mask, int Left, int Top) Rasterize(GlyphOutline outline, double scale, double subpixelX)
    {
        var (xMin, yMin, xMax, yMax) = outline.Bounds();
        if (outline.PointCount == 0 || xMax <= xMin || yMax <= yMin)
        {
            return (new AlphaMask(0, 0), 0, 0);
        }

        var left = (int)Math.Floor((xMin * scale) + subpixelX);
        var right = (int)Math.Ceiling((xMax * scale) + subpixelX);
        var top = (int)Math.Floor(-yMax * scale);
        var bottom = (int)Math.Ceiling(-yMin * scale);
        var width = Math.Max(1, right - left);
        var height = Math.Max(1, bottom - top);
        var mask = Fill(outline.Segments(scale, subpixelX - left, -top, FlatnessTolerance), width, height);
        return (mask, left, top);
    }

    public static AlphaMask Fill(IEnumerable<(double X0, double Y0, double X1, double Y1)> segments, int width, int height)
    {
        var stride = width + 2;
        var accumulation = new double[stride * height];
        var crossings = new List<double>();
        foreach (var (x0, y0, x1, y1) in segments)
        {
            AddLine(accumulation, stride, width, height, x0, y0, x1, y1, crossings);
        }

        var coverage = new byte[width * height];
        for (var row = 0; row < height; row++)
        {
            var sum = 0.0;
            for (var x = 0; x < width; x++)
            {
                sum += accumulation[(row * stride) + x];
                coverage[(row * width) + x] = (byte)Math.Round(Math.Min(1.0, Math.Abs(sum)) * 255);
            }
        }

        return new AlphaMask(width, height, coverage);
    }

    private static void AddLine(double[] accumulation, int stride, int width, int height, double x0, double y0, double x1, double y1, List<double> crossings)
    {
        if (y0 == y1)
        {
            return;
        }

        crossings.Clear();
        crossings.Add(0);
        crossings.Add(1);
        AddCrossings(crossings, x0, x1);
        AddCrossings(crossings, y0, y1);
        crossings.Sort();

        for (var i = 1; i < crossings.Count; i++)
        {
            var t0 = crossings[i - 1];
            var t1 = crossings[i];
            if (t1 <= t0)
            {
                continue;
            }

            var ax = x0 + ((x1 - x0) * t0);
            var ay = y0 + ((y1 - y0) * t0);
            var bx = x0 + ((x1 - x0) * t1);
            var by = y0 + ((y1 - y0) * t1);
            var dy = by - ay;
            var row = (int)Math.Floor((ay + by) / 2);
            if (dy == 0 || row < 0 || row >= height)
            {
                continue;
            }

            var middleX = (ax + bx) / 2;
            var column = (int)Math.Floor(middleX);
            var rowStart = row * stride;
            if (column < 0)
            {
                accumulation[rowStart] += dy;
            }
            else if (column < width)
            {
                var fraction = middleX - column;
                accumulation[rowStart + column] += dy * (1 - fraction);
                accumulation[rowStart + column + 1] += dy * fraction;
            }
        }
    }

    private static void AddCrossings(List<double> crossings, double from, double to)
    {
        if (from == to)
        {
            return;
        }

        var low = Math.Min(from, to);
        var high = Math.Max(from, to);
        for (var boundary = Math.Floor(low) + 1; boundary < high; boundary++)
        {
            crossings.Add((boundary - from) / (to - from));
        }
    }
}
