using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace EggEncoder.Codecs.H264;

[SkipLocalsInit]
internal static class InterPrediction
{
    public static void Luma(H264Picture reference, int x, int y, int xFrac, int yFrac, int width, int height, Span<byte> output, int outputStride)
    {
        var frame = reference.Frame;
        var pictureWidth = reference.WidthInMacroblocks * 16;
        var pictureHeight = reference.HeightInMacroblocks * 16;
        scoped ReadOnlySpan<byte> source;
        int origin;
        int stride;
        Span<byte> fetched = stackalloc byte[21 * 21];
        if (x - 2 >= 0 && y - 2 >= 0 && x + width + 3 <= pictureWidth && y + height + 3 <= pictureHeight)
        {
            source = frame.Buffer;
            stride = frame.StrideY;
            origin = frame.OffsetY + (y * stride) + x;
        }
        else
        {
            stride = width + 5;
            Fetch(frame.Buffer, frame.OffsetY, frame.StrideY, pictureWidth, pictureHeight, x - 2, y - 2, stride, height + 5, fetched);
            source = fetched;
            origin = (2 * stride) + 2;
        }

        if (xFrac == 0 && yFrac == 0)
        {
            for (var j = 0; j < height; j++)
            {
                source.Slice(origin + (j * stride), width).CopyTo(output.Slice(j * outputStride, width));
            }

            return;
        }

        var lanes = width >= 8 ? 8 : 4;
        if (yFrac == 0)
        {
            for (var j = 0; j < height; j++)
            {
                var row = origin + (j * stride);
                for (var i = 0; i < width; i += lanes)
                {
                    var b = HalfVector(source, row + i, 1, lanes);
                    if (xFrac != 2)
                    {
                        b = RoundedAverage(b, Load(source, row + i + (xFrac >> 1), lanes));
                    }

                    Store(output, (j * outputStride) + i, b, lanes);
                }
            }

            return;
        }

        if (xFrac == 0)
        {
            for (var j = 0; j < height; j++)
            {
                var row = origin + (j * stride);
                for (var i = 0; i < width; i += lanes)
                {
                    var h = HalfVector(source, row + i, stride, lanes);
                    if (yFrac != 2)
                    {
                        h = RoundedAverage(h, Load(source, row + i + ((yFrac >> 1) * stride), lanes));
                    }

                    Store(output, (j * outputStride) + i, h, lanes);
                }
            }

            return;
        }

        if (xFrac != 2 && yFrac != 2)
        {
            var bRow = yFrac >> 1;
            var hColumn = xFrac >> 1;
            for (var j = 0; j < height; j++)
            {
                var row = origin + (j * stride);
                for (var i = 0; i < width; i += lanes)
                {
                    var b = HalfVector(source, row + (bRow * stride) + i, 1, lanes);
                    var h = HalfVector(source, row + i + hColumn, stride, lanes);
                    Store(output, (j * outputStride) + i, RoundedAverage(b, h), lanes);
                }
            }

            return;
        }

        Span<short> intermediate = stackalloc short[(16 * 21) + 8];
        for (var j = -2; j < height + 3; j++)
        {
            var row = origin + (j * stride);
            for (var i = 0; i < width; i += lanes)
            {
                StoreWords(intermediate, ((j + 2) * width) + i, Tap6Vector(source, row + i, 1, lanes), lanes);
            }
        }

        var twenty = Vector128.Create(20);
        var five = Vector128.Create(5);
        var centerRound = Vector128.Create(512);
        var halfRound = Vector128.Create((short)16);
        var zero = Vector128<int>.Zero;
        var maximum = Vector128.Create(255);
        for (var j = 0; j < height; j++)
        {
            var row = origin + (j * stride);
            for (var i = 0; i < width; i += lanes)
            {
                var index = ((j + 2) * width) + i;
                var t0 = LoadWords(intermediate, index - (2 * width));
                var t1 = LoadWords(intermediate, index - width);
                var t2 = LoadWords(intermediate, index);
                var t3 = LoadWords(intermediate, index + width);
                var t4 = LoadWords(intermediate, index + (2 * width));
                var t5 = LoadWords(intermediate, index + (3 * width));
                var low = Tap6Wide(Vector128.WidenLower(t0), Vector128.WidenLower(t1), Vector128.WidenLower(t2), Vector128.WidenLower(t3), Vector128.WidenLower(t4), Vector128.WidenLower(t5), five, twenty);
                var high = Tap6Wide(Vector128.WidenUpper(t0), Vector128.WidenUpper(t1), Vector128.WidenUpper(t2), Vector128.WidenUpper(t3), Vector128.WidenUpper(t4), Vector128.WidenUpper(t5), five, twenty);
                low = Vector128.Min(Vector128.Max(Vector128.ShiftRightArithmetic(low + centerRound, 10), zero), maximum);
                high = Vector128.Min(Vector128.Max(Vector128.ShiftRightArithmetic(high + centerRound, 10), zero), maximum);
                var center = Vector128.Narrow(low, high);
                if (xFrac == 2 && yFrac != 2)
                {
                    var b = LoadWords(intermediate, index + ((yFrac >> 1) * width));
                    b = Vector128.Min(Vector128.Max(Vector128.ShiftRightArithmetic(b + halfRound, 5), Vector128<short>.Zero), Vector128.Create((short)255));
                    center = RoundedAverage(center, b);
                }
                else if (yFrac == 2 && xFrac != 2)
                {
                    center = RoundedAverage(center, HalfVector(source, row + i + (xFrac >> 1), stride, lanes));
                }

                Store(output, (j * outputStride) + i, center, lanes);
            }
        }
    }

    public static void Chroma(H264Picture reference, bool cr, int x, int y, int xFrac, int yFrac, int width, int height, Span<byte> output, int outputStride)
    {
        var frame = reference.Frame;
        var pictureWidth = reference.WidthInMacroblocks * 8;
        var pictureHeight = reference.HeightInMacroblocks * 8;
        var planeOffset = cr ? frame.OffsetV : frame.OffsetU;
        scoped ReadOnlySpan<byte> source;
        int origin;
        int stride;
        Span<byte> fetched = stackalloc byte[9 * 9];
        if (x >= 0 && y >= 0 && x + width + 1 <= pictureWidth && y + height + 1 <= pictureHeight)
        {
            source = frame.Buffer;
            stride = frame.StrideUV;
            origin = planeOffset + (y * stride) + x;
        }
        else
        {
            stride = width + 1;
            Fetch(frame.Buffer, planeOffset, frame.StrideUV, pictureWidth, pictureHeight, x, y, stride, height + 1, fetched);
            source = fetched;
            origin = 0;
        }

        if (xFrac == 0 && yFrac == 0)
        {
            for (var j = 0; j < height; j++)
            {
                source.Slice(origin + (j * stride), width).CopyTo(output.Slice(j * outputStride, width));
            }

            return;
        }

        var a = (8 - xFrac) * (8 - yFrac);
        var b = xFrac * (8 - yFrac);
        var c = (8 - xFrac) * yFrac;
        var d = xFrac * yFrac;
        if (width >= 4)
        {
            var lanes = width >= 8 ? 8 : 4;
            var weightA = Vector128.Create((short)a);
            var weightB = Vector128.Create((short)b);
            var weightC = Vector128.Create((short)c);
            var weightD = Vector128.Create((short)d);
            var round = Vector128.Create((short)32);
            for (var j = 0; j < height; j++)
            {
                var row = origin + (j * stride);
                var below = row + stride;
                var sum = (Load(source, row, lanes) * weightA) + (Load(source, row + 1, lanes) * weightB)
                    + (Load(source, below, lanes) * weightC) + (Load(source, below + 1, lanes) * weightD) + round;
                Store(output, j * outputStride, Vector128.ShiftRightLogical(sum, 6), lanes);
            }

            return;
        }

        for (var j = 0; j < height; j++)
        {
            var row = origin + (j * stride);
            for (var i = 0; i < width; i++)
            {
                var index = row + i;
                output[(j * outputStride) + i] = (byte)(((a * source[index]) + (b * source[index + 1]) + (c * source[index + stride]) + (d * source[index + stride + 1]) + 32) >> 6);
            }
        }
    }

    public static void WeightExplicit(Span<byte> samples, int stride, int width, int height, int log2Denominator, int weight, int offset)
    {
        var round = log2Denominator >= 1 ? 1 << (log2Denominator - 1) : 0;
        var vectorWeight = Vector128.Create(weight);
        var vectorRound = Vector128.Create(round);
        var vectorOffset = Vector128.Create(offset);
        for (var j = 0; j < height; j++)
        {
            var row = samples.Slice(j * stride, width);
            var i = 0;
            for (; i + 8 <= width; i += 8)
            {
                var (low, high) = Widen8(row[i..]);
                Store8(
                    row[i..],
                    (Vector128.ShiftRightArithmetic((low * vectorWeight) + vectorRound, log2Denominator) + vectorOffset),
                    (Vector128.ShiftRightArithmetic((high * vectorWeight) + vectorRound, log2Denominator) + vectorOffset));
            }

            for (; i < width; i++)
            {
                row[i] = IntraPrediction.Clip((((row[i] * weight) + round) >> log2Denominator) + offset);
            }
        }
    }

    public static void Bipredict(Span<byte> first, int firstStride, ReadOnlySpan<byte> second, int secondStride, int width, int height, int log2Denominator, int weight0, int weight1, int offset0, int offset1)
    {
        var offset = (offset0 + offset1 + 1) >> 1;
        var round = 1 << log2Denominator;
        var shift = log2Denominator + 1;
        var vectorWeight0 = Vector128.Create(weight0);
        var vectorWeight1 = Vector128.Create(weight1);
        var vectorRound = Vector128.Create(round);
        var vectorOffset = Vector128.Create(offset);
        for (var j = 0; j < height; j++)
        {
            var target = first.Slice(j * firstStride, width);
            var source = second.Slice(j * secondStride, width);
            var i = 0;
            for (; i + 8 <= width; i += 8)
            {
                var (lowA, highA) = Widen8(target[i..]);
                var (lowB, highB) = Widen8(source[i..]);
                Store8(
                    target[i..],
                    Vector128.ShiftRightArithmetic((lowA * vectorWeight0) + (lowB * vectorWeight1) + vectorRound, shift) + vectorOffset,
                    Vector128.ShiftRightArithmetic((highA * vectorWeight0) + (highB * vectorWeight1) + vectorRound, shift) + vectorOffset);
            }

            for (; i < width; i++)
            {
                target[i] = IntraPrediction.Clip((((target[i] * weight0) + (source[i] * weight1) + round) >> shift) + offset);
            }
        }
    }

    public static void BipredictImplicit(Span<byte> first, int firstStride, ReadOnlySpan<byte> second, int secondStride, int width, int height, int weight0, int weight1)
    {
        if (width < 4)
        {
            Bipredict(first, firstStride, second, secondStride, width, height, 5, weight0, weight1, 0, 0);
            return;
        }

        var lanes = width >= 8 ? 8 : 4;
        var vectorWeight0 = Vector128.Create((short)weight0);
        var vectorWeight1 = Vector128.Create((short)weight1);
        var round = Vector128.Create((short)32);
        for (var j = 0; j < height; j++)
        {
            var target = first.Slice(j * firstStride, width);
            var source = second.Slice(j * secondStride, width);
            for (var i = 0; i < width; i += lanes)
            {
                var sum = (Load(target, i, lanes) * vectorWeight0) + (Load(source, i, lanes) * vectorWeight1) + round;
                Store(target, i, Vector128.ShiftRightArithmetic(sum, 6), lanes);
            }
        }
    }

    public static void AverageInto(Span<byte> first, int firstStride, ReadOnlySpan<byte> second, int secondStride, int width, int height)
    {
        for (var j = 0; j < height; j++)
        {
            var target = first.Slice(j * firstStride, width);
            var source = second.Slice(j * secondStride, width);
            var i = 0;
            if (width >= 16)
            {
                for (; i + 16 <= width; i += 16)
                {
                    var a = Vector128.Create<byte>(target[i..]);
                    var b = Vector128.Create<byte>(source[i..]);
                    ((a | b) - Vector128.ShiftRightLogical(a ^ b, 1)).CopyTo(target[i..]);
                }
            }

            for (; i + 8 <= width; i += 8)
            {
                var a = BinaryPrimitives.ReadUInt64LittleEndian(target[i..]);
                var b = BinaryPrimitives.ReadUInt64LittleEndian(source[i..]);
                BinaryPrimitives.WriteUInt64LittleEndian(target[i..], (a | b) - (((a ^ b) >> 1) & 0x7F7F7F7F7F7F7F7FUL));
            }

            for (; i < width; i++)
            {
                target[i] = (byte)((target[i] + source[i] + 1) >> 1);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Load(ReadOnlySpan<byte> source, int index, int lanes)
    {
        var raw = lanes == 8
            ? Vector128.CreateScalar(BinaryPrimitives.ReadUInt64LittleEndian(source[index..])).AsByte()
            : Vector128.CreateScalar(BinaryPrimitives.ReadUInt32LittleEndian(source[index..])).AsByte();
        return Vector128.WidenLower(raw).AsInt16();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store(Span<byte> output, int index, Vector128<short> values, int lanes)
    {
        var clamped = Vector128.Min(Vector128.Max(values, Vector128<short>.Zero), Vector128.Create((short)255)).AsUInt16();
        var bytes = Vector128.Narrow(clamped, clamped).AsUInt64().ToScalar();
        if (lanes == 8)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(output[index..], bytes);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output[index..], (uint)bytes);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Tap6Vector(ReadOnlySpan<byte> source, int index, int step, int lanes) =>
        Load(source, index - (2 * step), lanes) + Load(source, index + (3 * step), lanes)
            - (Vector128.Create((short)5) * (Load(source, index - step, lanes) + Load(source, index + (2 * step), lanes)))
            + (Vector128.Create((short)20) * (Load(source, index, lanes) + Load(source, index + step, lanes)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Tap6Wide(Vector128<int> t0, Vector128<int> t1, Vector128<int> t2, Vector128<int> t3, Vector128<int> t4, Vector128<int> t5, Vector128<int> five, Vector128<int> twenty) =>
        t0 + t5 - (five * (t1 + t4)) + (twenty * (t2 + t3));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> LoadWords(ReadOnlySpan<short> source, int index) =>
        Vector128.Create(source.Slice(index, Vector128<short>.Count));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreWords(Span<short> output, int index, Vector128<short> values, int lanes)
    {
        if (lanes == 8)
        {
            values.CopyTo(output[index..]);
        }
        else
        {
            BinaryPrimitives.WriteUInt64LittleEndian(System.Runtime.InteropServices.MemoryMarshal.AsBytes(output[index..]), values.AsUInt64().ToScalar());
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> HalfVector(ReadOnlySpan<byte> source, int index, int step, int lanes)
    {
        var sum = Tap6Vector(source, index, step, lanes);
        var shifted = Vector128.ShiftRightArithmetic(sum + Vector128.Create((short)16), 5);
        return Vector128.Min(Vector128.Max(shifted, Vector128<short>.Zero), Vector128.Create((short)255));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> RoundedAverage(Vector128<short> a, Vector128<short> b) =>
        Vector128.ShiftRightArithmetic(a + b + Vector128.Create((short)1), 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (Vector128<int> Low, Vector128<int> High) Widen8(ReadOnlySpan<byte> source)
    {
        var bytes = Vector128.CreateScalar(BinaryPrimitives.ReadUInt64LittleEndian(source)).AsByte();
        var words = Vector128.WidenLower(bytes);
        return (Vector128.WidenLower(words).AsInt32(), Vector128.WidenUpper(words).AsInt32());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store8(Span<byte> target, Vector128<int> low, Vector128<int> high)
    {
        var zero = Vector128<int>.Zero;
        var maximum = Vector128.Create(255);
        var words = Vector128.Narrow(Vector128.Min(Vector128.Max(low, zero), maximum), Vector128.Min(Vector128.Max(high, zero), maximum));
        var bytes = Vector128.Narrow(words.AsUInt16(), words.AsUInt16());
        BinaryPrimitives.WriteUInt64LittleEndian(target, bytes.AsUInt64().ToScalar());
    }

    private static void Fetch(byte[] buffer, int offset, int stride, int pictureWidth, int pictureHeight, int x, int y, int width, int height, Span<byte> output)
    {
        for (var j = 0; j < height; j++)
        {
            var row = offset + (Math.Clamp(y + j, 0, pictureHeight - 1) * stride);
            for (var i = 0; i < width; i++)
            {
                output[(j * width) + i] = buffer[row + Math.Clamp(x + i, 0, pictureWidth - 1)];
            }
        }
    }
}
