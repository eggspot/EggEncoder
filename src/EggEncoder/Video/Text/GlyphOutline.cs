namespace EggEncoder.Video.Text;

internal sealed class GlyphOutline
{
    private readonly List<double> _x = [];
    private readonly List<double> _y = [];
    private readonly List<bool> _onCurve = [];
    private readonly List<int> _contourEnds = [];

    public int PointCount => _x.Count;

    public IReadOnlyList<int> ContourEnds => _contourEnds;

    public double X(int point) => _x[point];

    public double Y(int point) => _y[point];

    public bool IsOnCurve(int point) => _onCurve[point];

    public void AddPoint(double x, double y, bool onCurve)
    {
        _x.Add(x);
        _y.Add(y);
        _onCurve.Add(onCurve);
    }

    public void EndContour(int lastPoint) => _contourEnds.Add(lastPoint);

    public void Shift(int fromPoint, double dx, double dy)
    {
        for (var i = fromPoint; i < _x.Count; i++)
        {
            _x[i] += dx;
            _y[i] += dy;
        }
    }

    public (double XMin, double YMin, double XMax, double YMax) Bounds()
    {
        if (_x.Count == 0)
        {
            return (0, 0, 0, 0);
        }

        return (_x.Min(), _y.Min(), _x.Max(), _y.Max());
    }

    public IEnumerable<(double X0, double Y0, double X1, double Y1)> Segments(double scale, double offsetX, double offsetY, double tolerance)
    {
        var start = 0;
        foreach (var end in _contourEnds)
        {
            foreach (var segment in ContourSegments(start, end, scale, offsetX, offsetY, tolerance))
            {
                yield return segment;
            }

            start = end + 1;
        }
    }

    private IEnumerable<(double, double, double, double)> ContourSegments(int start, int end, double scale, double offsetX, double offsetY, double tolerance)
    {
        var count = end - start + 1;
        if (count < 2)
        {
            yield break;
        }

        (double X, double Y) Point(int index)
        {
            var i = start + (index % count);
            return ((_x[i] * scale) + offsetX, offsetY - (_y[i] * scale));
        }

        var first = Enumerable.Range(0, count).FirstOrDefault(i => _onCurve[start + i], -1);
        (double X, double Y) startPoint;
        int sequenceStart;
        if (first < 0)
        {
            var a = Point(0);
            var b = Point(1);
            startPoint = ((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            sequenceStart = 1;
        }
        else
        {
            startPoint = Point(first);
            sequenceStart = first + 1;
        }

        var sequenceLength = first < 0 ? count : count - 1;
        var current = startPoint;
        (double X, double Y)? control = null;
        for (var step = 0; step <= sequenceLength; step++)
        {
            var isClosing = step == sequenceLength;
            var point = isClosing ? startPoint : Point(sequenceStart + step);
            var onCurve = isClosing || _onCurve[start + ((sequenceStart + step) % count)];
            if (onCurve)
            {
                if (control is { } c)
                {
                    foreach (var segment in Curve(current, c, point, tolerance))
                    {
                        yield return segment;
                    }
                }
                else
                {
                    yield return (current.X, current.Y, point.X, point.Y);
                }

                current = point;
                control = null;
            }
            else if (control is { } previous)
            {
                var middle = ((previous.X + point.X) / 2, (previous.Y + point.Y) / 2);
                foreach (var segment in Curve(current, previous, middle, tolerance))
                {
                    yield return segment;
                }

                current = middle;
                control = point;
            }
            else
            {
                control = point;
            }
        }
    }

    private static IEnumerable<(double, double, double, double)> Curve((double X, double Y) p0, (double X, double Y) p1, (double X, double Y) p2, double tolerance)
    {
        var ddx = p0.X - (2 * p1.X) + p2.X;
        var ddy = p0.Y - (2 * p1.Y) + p2.Y;
        var steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(Math.Sqrt((ddx * ddx) + (ddy * ddy)) / (8 * tolerance))));
        var previous = p0;
        for (var i = 1; i <= steps; i++)
        {
            var t = i / (double)steps;
            var u = 1 - t;
            var next = ((u * u * p0.X) + (2 * u * t * p1.X) + (t * t * p2.X), (u * u * p0.Y) + (2 * u * t * p1.Y) + (t * t * p2.Y));
            yield return (previous.X, previous.Y, next.Item1, next.Item2);
            previous = next;
        }
    }
}
