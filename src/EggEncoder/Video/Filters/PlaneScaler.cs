using System.Numerics;

namespace EggEncoder.Video.Filters;

internal sealed class PlaneScaler
{
    private const int WeightBits = 14;
    private const int WeightOne = 1 << WeightBits;
    private const int IntermediateShift = 8;
    private const int OutputShift = (2 * WeightBits) - IntermediateShift;

    private readonly AxisWeights _horizontal;
    private readonly AxisWeights _vertical;
    private readonly int _destinationWidth;
    private readonly int _destinationHeight;
    private short[] _intermediate = [];

    private PlaneScaler(AxisWeights horizontal, AxisWeights vertical, int destinationWidth, int destinationHeight)
    {
        _horizontal = horizontal;
        _vertical = vertical;
        _destinationWidth = destinationWidth;
        _destinationHeight = destinationHeight;
    }

    public static (PlaneScaler Luma, PlaneScaler Chroma) Create(
        ScaleKernel kernel, int sourceWidth, int sourceHeight, VideoRectangle source, int destinationWidth, int destinationHeight)
    {
        var scaleX = source.Width / (double)destinationWidth;
        var scaleY = source.Height / (double)destinationHeight;
        var luma = new PlaneScaler(
            AxisWeights.Build(kernel, destinationWidth, scaleX, n => source.X + ((n + 0.5) * scaleX) - 0.5, source.X, source.Right - 1),
            AxisWeights.Build(kernel, destinationHeight, scaleY, n => source.Y + ((n + 0.5) * scaleY) - 0.5, source.Y, source.Bottom - 1),
            destinationWidth,
            destinationHeight);

        var chromaWidth = (destinationWidth + 1) / 2;
        var chromaHeight = (destinationHeight + 1) / 2;
        var sourceChromaRight = Math.Min((sourceWidth + 1) / 2, (source.Right + 1) / 2) - 1;
        var sourceChromaBottom = Math.Min((sourceHeight + 1) / 2, (source.Bottom + 1) / 2) - 1;
        var chroma = new PlaneScaler(
            AxisWeights.Build(kernel, chromaWidth, scaleX, n => (source.X + (((2 * n) + 0.5) * scaleX) - 0.5) / 2, source.X / 2, sourceChromaRight),
            AxisWeights.Build(kernel, chromaHeight, scaleY, n => (source.Y + (((2 * n) + 1) * scaleY) - 1) / 2, source.Y / 2, sourceChromaBottom),
            chromaWidth,
            chromaHeight);
        return (luma, chroma);
    }

    public void Scale(byte[] source, int sourceOffset, int sourceStride, byte[] destination, int destinationOffset, int destinationStride, int maxDegreeOfParallelism)
    {
        var firstRow = _vertical.Minimum;
        var rows = _vertical.Maximum - firstRow + 1;
        var needed = rows * _destinationWidth;
        if (_intermediate.Length < needed)
        {
            _intermediate = new short[needed];
        }

        var intermediate = _intermediate;
        FrameTransformSupport.ForRows(rows, maxDegreeOfParallelism, (start, end) =>
        {
            for (var row = start; row < end; row++)
            {
                ScaleRow(source, sourceOffset + ((firstRow + row) * sourceStride), intermediate.AsSpan(row * _destinationWidth, _destinationWidth));
            }
        });

        FrameTransformSupport.ForRows(_destinationHeight, maxDegreeOfParallelism, (start, end) =>
        {
            var accumulator = new int[_destinationWidth];
            for (var row = start; row < end; row++)
            {
                ScaleColumn(intermediate, row, firstRow, accumulator, destination.AsSpan(destinationOffset + (row * destinationStride), _destinationWidth));
            }
        });
    }

    private unsafe void ScaleRow(byte[] source, int rowOffset, Span<short> output)
    {
        var taps = _horizontal.Taps;
        const int rounding = 1 << (IntermediateShift - 1);
        fixed (byte* sourceRow = &source[rowOffset])
        fixed (short* weightsStart = _horizontal.Weights)
        fixed (int* starts = _horizontal.Starts)
        fixed (short* outputStart = output)
        {
            var weights = weightsStart;
            if (taps == 4)
            {
                for (var x = 0; x < output.Length; x++, weights += 4)
                {
                    var input = sourceRow + starts[x];
                    var sum = (input[0] * weights[0]) + (input[1] * weights[1]) + (input[2] * weights[2]) + (input[3] * weights[3]);
                    outputStart[x] = (short)((sum + rounding) >> IntermediateShift);
                }

                return;
            }

            for (var x = 0; x < output.Length; x++, weights += taps)
            {
                var input = sourceRow + starts[x];
                var sum = 0;
                for (var tap = 0; tap < taps; tap++)
                {
                    sum += input[tap] * weights[tap];
                }

                outputStart[x] = (short)((sum + rounding) >> IntermediateShift);
            }
        }
    }

    private void ScaleColumn(short[] intermediate, int row, int firstRow, int[] accumulator, Span<byte> output)
    {
        var width = _destinationWidth;
        var taps = _vertical.Taps;
        var start = _vertical.Starts[row] - firstRow;
        var weights = _vertical.Weights.AsSpan(row * taps, taps);
        var x = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var block = Vector<byte>.Count;
            var halfBlock = Vector<short>.Count;
            var rounding = new Vector<int>(1 << (OutputShift - 1));
            var zero = Vector<int>.Zero;
            var maximum = new Vector<int>(255);
            for (; x <= width - block; x += block)
            {
                var a = Vector<int>.Zero;
                var b = Vector<int>.Zero;
                var c = Vector<int>.Zero;
                var d = Vector<int>.Zero;
                for (var tap = 0; tap < taps; tap++)
                {
                    var weight = new Vector<int>(weights[tap]);
                    var input = intermediate.AsSpan(((start + tap) * width) + x, block);
                    Vector.Widen(new Vector<short>(input), out var low, out var high);
                    a += low * weight;
                    b += high * weight;
                    Vector.Widen(new Vector<short>(input[halfBlock..]), out low, out high);
                    c += low * weight;
                    d += high * weight;
                }

                var first = Vector.Narrow(Clamp(a), Clamp(b));
                var second = Vector.Narrow(Clamp(c), Clamp(d));
                Vector.Narrow(first, second).CopyTo(output[x..]);

                Vector<uint> Clamp(Vector<int> value) =>
                    Vector.AsVectorUInt32(Vector.Min(Vector.Max(Vector.ShiftRightArithmetic(value + rounding, OutputShift), zero), maximum));
            }
        }

        if (x == width)
        {
            return;
        }

        accumulator.AsSpan(x, width - x).Clear();
        for (var tap = 0; tap < taps; tap++)
        {
            int weight = weights[tap];
            var input = intermediate.AsSpan((start + tap) * width, width);
            for (var i = x; i < width; i++)
            {
                accumulator[i] += input[i] * weight;
            }
        }

        for (; x < width; x++)
        {
            output[x] = (byte)Math.Clamp((accumulator[x] + (1 << (OutputShift - 1))) >> OutputShift, 0, 255);
        }
    }

    internal sealed class AxisWeights
    {
        private AxisWeights(int taps, int[] starts, short[] weights, int minimum, int maximum)
        {
            Taps = taps;
            Starts = starts;
            Weights = weights;
            Minimum = minimum;
            Maximum = maximum;
        }

        public int Taps { get; }

        public int[] Starts { get; }

        public short[] Weights { get; }

        public int Minimum { get; }

        public int Maximum { get; }

        public static AxisWeights Build(ScaleKernel kernel, int outputs, double scale, Func<int, double> sourcePosition, int lowest, int highest)
        {
            var radius = KernelRadius(kernel);
            var stretch = Math.Max(1.0, scale);
            var support = radius * stretch;
            var available = highest - lowest + 1;
            var taps = Math.Min(available, (int)Math.Ceiling(2 * support) + 1);
            var starts = new int[outputs];
            var weights = new short[outputs * taps];
            var raw = new double[taps];
            var minimum = int.MaxValue;
            var maximum = int.MinValue;

            for (var n = 0; n < outputs; n++)
            {
                var position = sourcePosition(n);
                var first = (int)Math.Floor(position - support) + 1;
                var start = Math.Clamp(first, lowest, highest - taps + 1);
                Array.Clear(raw);
                for (var index = first; index <= (int)Math.Floor(position + support); index++)
                {
                    var weight = Kernel(kernel, (index - position) / stretch);
                    var clamped = Math.Clamp(index, lowest, highest) - start;
                    raw[Math.Clamp(clamped, 0, taps - 1)] += weight;
                }

                starts[n] = start;
                Quantize(raw, weights.AsSpan(n * taps, taps));
                minimum = Math.Min(minimum, start);
                maximum = Math.Max(maximum, start + taps - 1);
            }

            return new AxisWeights(taps, starts, weights, minimum, maximum);
        }

        private static void Quantize(double[] raw, Span<short> output)
        {
            var total = raw.Sum();
            if (total == 0)
            {
                output.Clear();
                output[raw.Length / 2] = WeightOne;
                return;
            }

            var sum = 0;
            var largest = 0;
            for (var i = 0; i < raw.Length; i++)
            {
                output[i] = (short)Math.Round(raw[i] / total * WeightOne);
                sum += output[i];
                if (Math.Abs(raw[i]) > Math.Abs(raw[largest]))
                {
                    largest = i;
                }
            }

            output[largest] += (short)(WeightOne - sum);
        }

        private static double KernelRadius(ScaleKernel kernel) => kernel switch
        {
            ScaleKernel.Bilinear => 1,
            ScaleKernel.Bicubic => 2,
            ScaleKernel.Lanczos => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(kernel), kernel, "Unknown scale kernel")
        };

        private static double Kernel(ScaleKernel kernel, double x)
        {
            var distance = Math.Abs(x);
            switch (kernel)
            {
                case ScaleKernel.Bilinear:
                    return Math.Max(0, 1 - distance);
                case ScaleKernel.Bicubic:
                    const double a = -0.6;
                    return distance < 1 ? ((((a + 2) * distance) - (a + 3)) * distance * distance) + 1
                        : distance < 2 ? (((((a * distance) - (5 * a)) * distance) + (8 * a)) * distance) - (4 * a)
                        : 0;
                default:
                    if (distance < 1e-9)
                    {
                        return 1;
                    }

                    if (distance >= 3)
                    {
                        return 0;
                    }

                    var px = Math.PI * x;
                    return 3 * Math.Sin(px) * Math.Sin(px / 3) / (px * px);
            }
        }
    }
}
