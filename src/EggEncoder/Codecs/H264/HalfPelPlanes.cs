using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace EggEncoder.Codecs.H264;

[SkipLocalsInit]
internal sealed class HalfPelPlanes
{
    public const int Margin = 29;

    private readonly int _width;
    private readonly int _height;

    private HalfPelPlanes(int width, int height)
    {
        _width = width;
        _height = height;
        Stride = width + (2 * Margin) + 8;
        Origin = (Margin * Stride) + Margin;
        var length = Stride * (height + (2 * Margin));
        Horizontal = new byte[length];
        Vertical = new byte[length];
        Center = new byte[length];
    }

    public byte[] Horizontal { get; }

    public byte[] Vertical { get; }

    public byte[] Center { get; }

    public int Stride { get; }

    public int Origin { get; }

    public static HalfPelPlanes Build(H264Picture picture, int parallelism, HalfPelPlanes? reuse)
    {
        var frame = picture.Frame;
        var planes = reuse is not null && reuse._width == frame.Width && reuse._height == frame.Height ? reuse : new HalfPelPlanes(frame.Width, frame.Height);
        var rows = frame.Height + (2 * Margin);
        var bands = Math.Clamp(parallelism, 1, 16);
        if (bands == 1)
        {
            planes.FillRows(picture, 0, rows);
        }
        else
        {
            Parallel.For(0, bands, new ParallelOptions { MaxDegreeOfParallelism = bands }, band =>
                planes.FillRows(picture, rows * band / bands, rows * (band + 1) / bands));
        }

        return planes;
    }

    private void FillRows(H264Picture picture, int firstRow, int endRow)
    {
        var frame = picture.Frame;
        var source = frame.Buffer;
        var stride = frame.StrideY;
        var columns = _width + (2 * Margin);
        Span<short> taps = stackalloc short[6 * (columns + 8)];
        var tapStride = columns + 8;
        for (var row = firstRow; row < endRow; row++)
        {
            var y = row - Margin;
            for (var k = row == firstRow ? 0 : 5; k < 6; k++)
            {
                var sourceRow = frame.OffsetY + ((y - 2 + k) * stride) - Margin;
                var tapRow = taps.Slice(Slot(y - 2 + k) * tapStride, tapStride);
                for (var x = 0; x < columns; x += 8)
                {
                    HorizontalTaps(source, sourceRow + x, tapRow[x..]);
                }
            }

            var target = (row * Stride) + 0;
            var centerRow = frame.OffsetY + (y * stride) - Margin;
            for (var x = 0; x < columns; x += 8)
            {
                var b = Vector128.Create<short>(taps.Slice((Slot(y) * tapStride) + x, 8));
                Store(Horizontal, target + x, Vector128.ShiftRightArithmetic(b + Vector128.Create((short)16), 5));
                var vertical = Load(source, centerRow + x - (2 * stride)) + Load(source, centerRow + x + (3 * stride))
                    - (Vector128.Create((short)5) * (Load(source, centerRow + x - stride) + Load(source, centerRow + x + (2 * stride))))
                    + (Vector128.Create((short)20) * (Load(source, centerRow + x) + Load(source, centerRow + x + stride)));
                Store(Vertical, target + x, Vector128.ShiftRightArithmetic(vertical + Vector128.Create((short)16), 5));
                var (low, high) = CenterTaps(taps, tapStride, x, y);
                var lowShifted = Vector128.ShiftRightArithmetic(low + Vector128.Create(512), 10);
                var highShifted = Vector128.ShiftRightArithmetic(high + Vector128.Create(512), 10);
                Store(Center, target + x, Vector128.Narrow(Vector128.Min(Vector128.Max(lowShifted, Vector128<int>.Zero), Vector128.Create(255)), Vector128.Min(Vector128.Max(highShifted, Vector128<int>.Zero), Vector128.Create(255))));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HorizontalTaps(byte[] source, int index, Span<short> output)
    {
        var sum = Load(source, index - 2) + Load(source, index + 3)
            - (Vector128.Create((short)5) * (Load(source, index - 1) + Load(source, index + 2)))
            + (Vector128.Create((short)20) * (Load(source, index) + Load(source, index + 1)));
        sum.CopyTo(output);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Slot(int row) => ((row % 6) + 6) % 6;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (Vector128<int> Low, Vector128<int> High) CenterTaps(ReadOnlySpan<short> taps, int stride, int x, int y)
    {
        var low = Vector128<int>.Zero;
        var high = Vector128<int>.Zero;
        ReadOnlySpan<int> weights = [1, -5, 20, 20, -5, 1];
        for (var k = 0; k < 6; k++)
        {
            var row = Vector128.Create<short>(taps.Slice((Slot(y - 2 + k) * stride) + x, 8));
            var weight = Vector128.Create(weights[k]);
            low += Vector128.WidenLower(row) * weight;
            high += Vector128.WidenUpper(row) * weight;
        }

        return (low, high);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Load(byte[] source, int index) =>
        Vector128.WidenLower(Vector128.CreateScalar(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(source.AsSpan(index))).AsByte()).AsInt16();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store(byte[] target, int index, Vector128<short> values)
    {
        var clamped = Vector128.Min(Vector128.Max(values, Vector128<short>.Zero), Vector128.Create((short)255)).AsUInt16();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(target.AsSpan(index), Vector128.Narrow(clamped, clamped).AsUInt64().ToScalar());
    }
}
