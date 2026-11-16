using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace EggEncoder.Codecs.H264;

internal static partial class DeblockingFilter
{
    private static bool CanFilterVectors => Sse2.IsSupported || AdvSimd.Arm64.IsSupported;

    private static void FilterLumaVectors(byte[] buffer, int start, int stride, bool vertical, ReadOnlySpan<byte> strengths, int alpha, int beta, int indexA)
    {
        for (var half = 0; half < 2; half++)
        {
            int first = strengths[2 * half];
            int second = strengths[(2 * half) + 1];
            if (first == 0 && second == 0)
            {
                continue;
            }

            var bS = Vector128.Create((short)first, (short)first, (short)first, (short)first, (short)second, (short)second, (short)second, (short)second);
            var tc0First = first is > 0 and < 4 ? (short)Tc0[first - 1][indexA] : (short)0;
            var tc0Second = second is > 0 and < 4 ? (short)Tc0[second - 1][indexA] : (short)0;
            var tc0 = Vector128.Create(tc0First, tc0First, tc0First, tc0First, tc0Second, tc0Second, tc0Second, tc0Second);
            var origin = vertical ? start + (half * 8 * stride) : start + (half * 8);
            var taps = LoadTaps(buffer, origin, stride, vertical);
            if (FilterLuma(ref taps, bS, tc0, alpha, beta))
            {
                StoreTaps(buffer, origin, stride, vertical, taps, wide: true);
            }
        }
    }

    private static void FilterChromaVectors(byte[] buffer, int start, int stride, bool vertical, ReadOnlySpan<byte> strengths, int alpha, int beta, int indexA)
    {
        var s0 = (short)strengths[0];
        var s1 = (short)strengths[1];
        var s2 = (short)strengths[2];
        var s3 = (short)strengths[3];
        var t0 = ChromaTc0(s0, indexA);
        var t1 = ChromaTc0(s1, indexA);
        var t2 = ChromaTc0(s2, indexA);
        var t3 = ChromaTc0(s3, indexA);
        var taps = LoadTaps(buffer, start, stride, vertical);
        if (FilterChroma(ref taps, Vector128.Create(s0, s0, s1, s1, s2, s2, s3, s3), Vector128.Create(t0, t0, t1, t1, t2, t2, t3, t3), alpha, beta))
        {
            StoreTaps(buffer, start, stride, vertical, taps, wide: false);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static short ChromaTc0(short strength, int indexA) => strength is > 0 and < 4 ? (short)Tc0[strength - 1][indexA] : (short)0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool FilterLuma(ref EdgeTaps taps, Vector128<short> bS, Vector128<short> tc0, int alpha, int beta)
    {
        var (p3, p2, p1, p0, q0, q1, q2, q3) = (taps.P3, taps.P2, taps.P1, taps.P0, taps.Q0, taps.Q1, taps.Q2, taps.Q3);
        var betaVector = Vector128.Create((short)beta);
        var filter = Vector128.LessThan(Vector128.Abs(p0 - q0), Vector128.Create((short)alpha))
            & Vector128.LessThan(Vector128.Abs(p1 - p0), betaVector)
            & Vector128.LessThan(Vector128.Abs(q1 - q0), betaVector)
            & Vector128.GreaterThan(bS, Vector128<short>.Zero);
        if (filter == Vector128<short>.Zero)
        {
            return false;
        }

        var ap = Vector128.LessThan(Vector128.Abs(p2 - p0), betaVector);
        var aq = Vector128.LessThan(Vector128.Abs(q2 - q0), betaVector);
        var strongEdge = Vector128.Equals(bS, Vector128.Create((short)4));
        var zero = Vector128<short>.Zero;
        var maximum = Vector128.Create((short)255);
        var two = Vector128.Create((short)2);
        var four = Vector128.Create((short)4);

        var tc = tc0 - ap - aq;
        var delta = Vector128.ShiftRightArithmetic(Vector128.ShiftLeft(q0 - p0, 2) + (p1 - q1) + four, 3);
        delta = Vector128.Min(Vector128.Max(delta, -tc), tc);
        var normalP0 = Vector128.Min(Vector128.Max(p0 + delta, zero), maximum);
        var normalQ0 = Vector128.Min(Vector128.Max(q0 - delta, zero), maximum);
        var average = Vector128.ShiftRightArithmetic(p0 + q0 + Vector128<short>.One, 1);
        var normalP1 = p1 + Vector128.Min(Vector128.Max(Vector128.ShiftRightArithmetic(p2 + average - Vector128.ShiftLeft(p1, 1), 1), -tc0), tc0);
        var normalQ1 = q1 + Vector128.Min(Vector128.Max(Vector128.ShiftRightArithmetic(q2 + average - Vector128.ShiftLeft(q1, 1), 1), -tc0), tc0);
        normalP1 = Vector128.ConditionalSelect(ap, normalP1, p1);
        normalQ1 = Vector128.ConditionalSelect(aq, normalQ1, q1);

        var strong = Vector128.LessThan(Vector128.Abs(p0 - q0), Vector128.Create((short)((alpha >> 2) + 2)));
        var strongP = ap & strong;
        var strongQ = aq & strong;
        var centre = p0 + q0;
        var three = Vector128.Create((short)3);
        var weakP0 = Vector128.ShiftRightArithmetic(Vector128.ShiftLeft(p1, 1) + p0 + q1 + two, 2);
        var weakQ0 = Vector128.ShiftRightArithmetic(Vector128.ShiftLeft(q1, 1) + q0 + p1 + two, 2);
        var intraP0 = Vector128.ConditionalSelect(strongP, Vector128.ShiftRightArithmetic(p2 + Vector128.ShiftLeft(p1 + centre, 1) + q1 + four, 3), weakP0);
        var intraP1 = Vector128.ConditionalSelect(strongP, Vector128.ShiftRightArithmetic(p2 + p1 + centre + two, 2), p1);
        var intraP2 = Vector128.ConditionalSelect(strongP, Vector128.ShiftRightArithmetic(Vector128.ShiftLeft(p3, 1) + (p2 * three) + p1 + centre + four, 3), p2);
        var intraQ0 = Vector128.ConditionalSelect(strongQ, Vector128.ShiftRightArithmetic(q2 + Vector128.ShiftLeft(q1 + centre, 1) + p1 + four, 3), weakQ0);
        var intraQ1 = Vector128.ConditionalSelect(strongQ, Vector128.ShiftRightArithmetic(q2 + q1 + centre + two, 2), q1);
        var intraQ2 = Vector128.ConditionalSelect(strongQ, Vector128.ShiftRightArithmetic(Vector128.ShiftLeft(q3, 1) + (q2 * three) + q1 + centre + four, 3), q2);

        var filterNormal = filter & ~strongEdge;
        var filterStrong = filter & strongEdge;
        taps.P0 = Vector128.ConditionalSelect(filterStrong, intraP0, Vector128.ConditionalSelect(filterNormal, normalP0, p0));
        taps.Q0 = Vector128.ConditionalSelect(filterStrong, intraQ0, Vector128.ConditionalSelect(filterNormal, normalQ0, q0));
        taps.P1 = Vector128.ConditionalSelect(filterStrong, intraP1, Vector128.ConditionalSelect(filterNormal, normalP1, p1));
        taps.Q1 = Vector128.ConditionalSelect(filterStrong, intraQ1, Vector128.ConditionalSelect(filterNormal, normalQ1, q1));
        taps.P2 = Vector128.ConditionalSelect(filterStrong, intraP2, p2);
        taps.Q2 = Vector128.ConditionalSelect(filterStrong, intraQ2, q2);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool FilterChroma(ref EdgeTaps taps, Vector128<short> bS, Vector128<short> tc0, int alpha, int beta)
    {
        var (p1, p0, q0, q1) = (taps.P1, taps.P0, taps.Q0, taps.Q1);
        var betaVector = Vector128.Create((short)beta);
        var filter = Vector128.LessThan(Vector128.Abs(p0 - q0), Vector128.Create((short)alpha))
            & Vector128.LessThan(Vector128.Abs(p1 - p0), betaVector)
            & Vector128.LessThan(Vector128.Abs(q1 - q0), betaVector)
            & Vector128.GreaterThan(bS, Vector128<short>.Zero);
        if (filter == Vector128<short>.Zero)
        {
            return false;
        }

        var zero = Vector128<short>.Zero;
        var maximum = Vector128.Create((short)255);
        var two = Vector128.Create((short)2);
        var tc = tc0 + Vector128<short>.One;
        var delta = Vector128.ShiftRightArithmetic(Vector128.ShiftLeft(q0 - p0, 2) + (p1 - q1) + Vector128.Create((short)4), 3);
        delta = Vector128.Min(Vector128.Max(delta, -tc), tc);
        var normalP0 = Vector128.Min(Vector128.Max(p0 + delta, zero), maximum);
        var normalQ0 = Vector128.Min(Vector128.Max(q0 - delta, zero), maximum);
        var strongEdge = Vector128.Equals(bS, Vector128.Create((short)4));
        var intraP0 = Vector128.ShiftRightArithmetic(Vector128.ShiftLeft(p1, 1) + p0 + q1 + two, 2);
        var intraQ0 = Vector128.ShiftRightArithmetic(Vector128.ShiftLeft(q1, 1) + q0 + p1 + two, 2);
        taps.P0 = Vector128.ConditionalSelect(filter, Vector128.ConditionalSelect(strongEdge, intraP0, normalP0), p0);
        taps.Q0 = Vector128.ConditionalSelect(filter, Vector128.ConditionalSelect(strongEdge, intraQ0, normalQ0), q0);
        return true;
    }

    private static EdgeTaps LoadTaps(byte[] buffer, int origin, int stride, bool vertical)
    {
        var span = buffer.AsSpan();
        if (!vertical)
        {
            return new EdgeTaps(
                LoadRow(span, origin - (4 * stride)),
                LoadRow(span, origin - (3 * stride)),
                LoadRow(span, origin - (2 * stride)),
                LoadRow(span, origin - stride),
                LoadRow(span, origin),
                LoadRow(span, origin + stride),
                LoadRow(span, origin + (2 * stride)),
                LoadRow(span, origin + (3 * stride)));
        }

        var (c01, c23, c45, c67) = Transpose8x8(
            LoadBytes(span, origin - 4),
            LoadBytes(span, origin - 4 + stride),
            LoadBytes(span, origin - 4 + (2 * stride)),
            LoadBytes(span, origin - 4 + (3 * stride)),
            LoadBytes(span, origin - 4 + (4 * stride)),
            LoadBytes(span, origin - 4 + (5 * stride)),
            LoadBytes(span, origin - 4 + (6 * stride)),
            LoadBytes(span, origin - 4 + (7 * stride)));
        return new EdgeTaps(
            Vector128.WidenLower(c01).AsInt16(),
            Vector128.WidenUpper(c01).AsInt16(),
            Vector128.WidenLower(c23).AsInt16(),
            Vector128.WidenUpper(c23).AsInt16(),
            Vector128.WidenLower(c45).AsInt16(),
            Vector128.WidenUpper(c45).AsInt16(),
            Vector128.WidenLower(c67).AsInt16(),
            Vector128.WidenUpper(c67).AsInt16());
    }

    private static void StoreTaps(byte[] buffer, int origin, int stride, bool vertical, in EdgeTaps taps, bool wide)
    {
        var span = buffer.AsSpan();
        if (!vertical)
        {
            if (wide)
            {
                StoreRow(span, origin - (3 * stride), taps.P2);
                StoreRow(span, origin + (2 * stride), taps.Q2);
            }

            StoreRow(span, origin - (2 * stride), taps.P1);
            StoreRow(span, origin - stride, taps.P0);
            StoreRow(span, origin, taps.Q0);
            StoreRow(span, origin + stride, taps.Q1);
            return;
        }

        var (r01, r23, r45, r67) = Transpose8x8(Narrow(taps.P3), Narrow(taps.P2), Narrow(taps.P1), Narrow(taps.P0), Narrow(taps.Q0), Narrow(taps.Q1), Narrow(taps.Q2), Narrow(taps.Q3));
        StoreRowPair(span, origin - 4, stride, r01.AsUInt64());
        StoreRowPair(span, origin - 4 + (2 * stride), stride, r23.AsUInt64());
        StoreRowPair(span, origin - 4 + (4 * stride), stride, r45.AsUInt64());
        StoreRowPair(span, origin - 4 + (6 * stride), stride, r67.AsUInt64());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreRowPair(Span<byte> span, int index, int stride, Vector128<ulong> rows)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(span[index..], rows.GetElement(0));
        BinaryPrimitives.WriteUInt64LittleEndian(span[(index + stride)..], rows.GetElement(1));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> LoadBytes(Span<byte> span, int index) =>
        Vector128.CreateScalar(BinaryPrimitives.ReadUInt64LittleEndian(span[index..])).AsByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> LoadRow(Span<byte> span, int index) => Vector128.WidenLower(LoadBytes(span, index)).AsInt16();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Narrow(Vector128<short> values)
    {
        var clamped = Vector128.Min(Vector128.Max(values, Vector128<short>.Zero), Vector128.Create((short)255)).AsUInt16();
        return Vector128.Narrow(clamped, clamped);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreRow(Span<byte> span, int index, Vector128<short> values) =>
        BinaryPrimitives.WriteUInt64LittleEndian(span[index..], Narrow(values).AsUInt64().ToScalar());

    private static (Vector128<byte> Columns01, Vector128<byte> Columns23, Vector128<byte> Columns45, Vector128<byte> Columns67) Transpose8x8(
        Vector128<byte> r0, Vector128<byte> r1, Vector128<byte> r2, Vector128<byte> r3, Vector128<byte> r4, Vector128<byte> r5, Vector128<byte> r6, Vector128<byte> r7)
    {
        var a0 = InterleaveLow(r0, r1).AsUInt16();
        var a1 = InterleaveLow(r2, r3).AsUInt16();
        var a2 = InterleaveLow(r4, r5).AsUInt16();
        var a3 = InterleaveLow(r6, r7).AsUInt16();
        var b0 = InterleaveLow(a0, a1).AsUInt32();
        var b1 = InterleaveHigh(a0, a1).AsUInt32();
        var b2 = InterleaveLow(a2, a3).AsUInt32();
        var b3 = InterleaveHigh(a2, a3).AsUInt32();
        return (
            InterleaveLow(b0, b2).AsByte(),
            InterleaveHigh(b0, b2).AsByte(),
            InterleaveLow(b1, b3).AsByte(),
            InterleaveHigh(b1, b3).AsByte());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> InterleaveLow(Vector128<byte> left, Vector128<byte> right) =>
        Sse2.IsSupported ? Sse2.UnpackLow(left, right) : AdvSimd.Arm64.ZipLow(left, right);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> InterleaveLow(Vector128<ushort> left, Vector128<ushort> right) =>
        Sse2.IsSupported ? Sse2.UnpackLow(left, right) : AdvSimd.Arm64.ZipLow(left, right);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> InterleaveHigh(Vector128<ushort> left, Vector128<ushort> right) =>
        Sse2.IsSupported ? Sse2.UnpackHigh(left, right) : AdvSimd.Arm64.ZipHigh(left, right);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> InterleaveLow(Vector128<uint> left, Vector128<uint> right) =>
        Sse2.IsSupported ? Sse2.UnpackLow(left, right) : AdvSimd.Arm64.ZipLow(left, right);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> InterleaveHigh(Vector128<uint> left, Vector128<uint> right) =>
        Sse2.IsSupported ? Sse2.UnpackHigh(left, right) : AdvSimd.Arm64.ZipHigh(left, right);

    private struct EdgeTaps(Vector128<short> p3, Vector128<short> p2, Vector128<short> p1, Vector128<short> p0, Vector128<short> q0, Vector128<short> q1, Vector128<short> q2, Vector128<short> q3)
    {
        public Vector128<short> P3 = p3;
        public Vector128<short> P2 = p2;
        public Vector128<short> P1 = p1;
        public Vector128<short> P0 = p0;
        public Vector128<short> Q0 = q0;
        public Vector128<short> Q1 = q1;
        public Vector128<short> Q2 = q2;
        public Vector128<short> Q3 = q3;
    }
}
