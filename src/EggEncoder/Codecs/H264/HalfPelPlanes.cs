using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using EggEncoder.Video;

namespace EggEncoder.Codecs.H264;

[SkipLocalsInit]
internal sealed class HalfPelPlanes
{
    public const int Margin = 29;

    private const int RowSlack = 16;

    private readonly int _width;
    private readonly int _height;

    private HalfPelPlanes(int width, int height)
    {
        _width = width;
        _height = height;
        Stride = width + (2 * Margin) + RowSlack;
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

    public int RowCount => _height + (2 * Margin);

    public static HalfPelPlanes ForFrame(VideoFrame frame, HalfPelPlanes? reuse) =>
        reuse is not null && reuse._width == frame.Width && reuse._height == frame.Height ? reuse : new HalfPelPlanes(frame.Width, frame.Height);

    public void Fill(H264Picture picture, int parallelism)
    {
        var rows = RowCount;
        var bands = Math.Clamp(parallelism, 1, 16);
        if (bands == 1)
        {
            FillRows(picture, 0, rows);
            return;
        }

        Parallel.For(0, bands, new ParallelOptions { MaxDegreeOfParallelism = bands }, band =>
            FillRows(picture, rows * band / bands, rows * (band + 1) / bands));
    }

    public void FillRows(H264Picture picture, int firstRow, int endRow)
    {
        if (Vector256.IsHardwareAccelerated)
        {
            FillRowsWide(picture, firstRow, endRow);
            return;
        }

        var frame = picture.Frame;
        var source = frame.Buffer;
        var stride = frame.StrideY;
        var columns = _width + (2 * Margin);
        Span<short> taps = stackalloc short[6 * (columns + RowSlack)];
        var tapStride = columns + RowSlack;
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
            ref var tapStart = ref MemoryMarshal.GetReference(taps);
            var row0 = (nuint)(Slot(y - 2) * tapStride);
            var row1 = (nuint)(Slot(y - 1) * tapStride);
            var row2 = (nuint)(Slot(y) * tapStride);
            var row3 = (nuint)(Slot(y + 1) * tapStride);
            var row4 = (nuint)(Slot(y + 2) * tapStride);
            var row5 = (nuint)(Slot(y + 3) * tapStride);
            for (var x = 0; x < columns; x += 8)
            {
                var column = (nuint)x;
                var t0 = Vector128.LoadUnsafe(ref tapStart, row0 + column);
                var t1 = Vector128.LoadUnsafe(ref tapStart, row1 + column);
                var t2 = Vector128.LoadUnsafe(ref tapStart, row2 + column);
                var t3 = Vector128.LoadUnsafe(ref tapStart, row3 + column);
                var t4 = Vector128.LoadUnsafe(ref tapStart, row4 + column);
                var t5 = Vector128.LoadUnsafe(ref tapStart, row5 + column);
                Store(Horizontal, target + x, Vector128.ShiftRightArithmetic(t2 + Vector128.Create((short)16), 5));
                var vertical = Load(source, centerRow + x - (2 * stride)) + Load(source, centerRow + x + (3 * stride))
                    - (Vector128.Create((short)5) * (Load(source, centerRow + x - stride) + Load(source, centerRow + x + (2 * stride))))
                    + (Vector128.Create((short)20) * (Load(source, centerRow + x) + Load(source, centerRow + x + stride)));
                Store(Vertical, target + x, Vector128.ShiftRightArithmetic(vertical + Vector128.Create((short)16), 5));
                var low = CenterTaps(Vector128.WidenLower(t0), Vector128.WidenLower(t1), Vector128.WidenLower(t2), Vector128.WidenLower(t3), Vector128.WidenLower(t4), Vector128.WidenLower(t5));
                var high = CenterTaps(Vector128.WidenUpper(t0), Vector128.WidenUpper(t1), Vector128.WidenUpper(t2), Vector128.WidenUpper(t3), Vector128.WidenUpper(t4), Vector128.WidenUpper(t5));
                Store(Center, target + x, Vector128.Narrow(low, high));
            }
        }
    }

    private void FillRowsWide(H264Picture picture, int firstRow, int endRow)
    {
        var frame = picture.Frame;
        var source = frame.Buffer;
        var stride = frame.StrideY;
        var columns = _width + (2 * Margin);
        var tapStride = columns + RowSlack;
        Span<short> taps = stackalloc short[6 * tapStride];
        ref var tapStart = ref MemoryMarshal.GetReference(taps);
        for (var row = firstRow; row < endRow; row++)
        {
            var y = row - Margin;
            for (var k = row == firstRow ? 0 : 5; k < 6; k++)
            {
                var sourceRow = frame.OffsetY + ((y - 2 + k) * stride) - Margin;
                var tapRow = (nuint)(Slot(y - 2 + k) * tapStride);
                for (var x = 0; x < columns; x += 16)
                {
                    var index = sourceRow + x;
                    var sum = LoadWide(source, index - 2) + LoadWide(source, index + 3)
                        - (Vector256.Create((short)5) * (LoadWide(source, index - 1) + LoadWide(source, index + 2)))
                        + (Vector256.Create((short)20) * (LoadWide(source, index) + LoadWide(source, index + 1)));
                    sum.StoreUnsafe(ref tapStart, tapRow + (nuint)x);
                }
            }

            var target = row * Stride;
            var centerRow = frame.OffsetY + (y * stride) - Margin;
            var row0 = (nuint)(Slot(y - 2) * tapStride);
            var row1 = (nuint)(Slot(y - 1) * tapStride);
            var row2 = (nuint)(Slot(y) * tapStride);
            var row3 = (nuint)(Slot(y + 1) * tapStride);
            var row4 = (nuint)(Slot(y + 2) * tapStride);
            var row5 = (nuint)(Slot(y + 3) * tapStride);
            for (var x = 0; x < columns; x += 16)
            {
                var column = (nuint)x;
                var t0 = Vector256.LoadUnsafe(ref tapStart, row0 + column);
                var t1 = Vector256.LoadUnsafe(ref tapStart, row1 + column);
                var t2 = Vector256.LoadUnsafe(ref tapStart, row2 + column);
                var t3 = Vector256.LoadUnsafe(ref tapStart, row3 + column);
                var t4 = Vector256.LoadUnsafe(ref tapStart, row4 + column);
                var t5 = Vector256.LoadUnsafe(ref tapStart, row5 + column);
                StoreWide(Horizontal, target + x, Vector256.ShiftRightArithmetic(t2 + Vector256.Create((short)16), 5));
                var vertical = LoadWide(source, centerRow + x - (2 * stride)) + LoadWide(source, centerRow + x + (3 * stride))
                    - (Vector256.Create((short)5) * (LoadWide(source, centerRow + x - stride) + LoadWide(source, centerRow + x + (2 * stride))))
                    + (Vector256.Create((short)20) * (LoadWide(source, centerRow + x) + LoadWide(source, centerRow + x + stride)));
                StoreWide(Vertical, target + x, Vector256.ShiftRightArithmetic(vertical + Vector256.Create((short)16), 5));
                var low = CenterTapsWide(Vector256.WidenLower(t0), Vector256.WidenLower(t1), Vector256.WidenLower(t2), Vector256.WidenLower(t3), Vector256.WidenLower(t4), Vector256.WidenLower(t5));
                var high = CenterTapsWide(Vector256.WidenUpper(t0), Vector256.WidenUpper(t1), Vector256.WidenUpper(t2), Vector256.WidenUpper(t3), Vector256.WidenUpper(t4), Vector256.WidenUpper(t5));
                var center = Vector256.Narrow(low, high).AsUInt16();
                Vector256.Narrow(center, center).GetLower().CopyTo(Center.AsSpan(target + x, 16));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> LoadWide(byte[] source, int index) =>
        Vector256.WidenLower(Vector128.Create<byte>(source.AsSpan(index, 16)).ToVector256Unsafe()).AsInt16();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreWide(byte[] target, int index, Vector256<short> values)
    {
        var clamped = Vector256.Min(Vector256.Max(values, Vector256<short>.Zero), Vector256.Create((short)255)).AsUInt16();
        Vector256.Narrow(clamped, clamped).GetLower().CopyTo(target.AsSpan(index, 16));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> CenterTapsWide(Vector256<int> t0, Vector256<int> t1, Vector256<int> t2, Vector256<int> t3, Vector256<int> t4, Vector256<int> t5)
    {
        var sum = (t0 + t5) - (Vector256.Create(5) * (t1 + t4)) + (Vector256.Create(20) * (t2 + t3));
        var shifted = Vector256.ShiftRightArithmetic(sum + Vector256.Create(512), 10);
        return Vector256.Min(Vector256.Max(shifted, Vector256<int>.Zero), Vector256.Create(255));
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
    private static Vector128<int> CenterTaps(Vector128<int> t0, Vector128<int> t1, Vector128<int> t2, Vector128<int> t3, Vector128<int> t4, Vector128<int> t5)
    {
        var sum = (t0 + t5) - (Vector128.Create(5) * (t1 + t4)) + (Vector128.Create(20) * (t2 + t3));
        var shifted = Vector128.ShiftRightArithmetic(sum + Vector128.Create(512), 10);
        return Vector128.Min(Vector128.Max(shifted, Vector128<int>.Zero), Vector128.Create(255));
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
