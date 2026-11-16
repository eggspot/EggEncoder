using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace EggEncoder.Codecs.H264;

internal static class IntegerTransposes
{
    public static bool IsSupported => Sse2.IsSupported || AdvSimd.Arm64.IsSupported;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (Vector128<int>, Vector128<int>, Vector128<int>, Vector128<int>) Transpose4x4(Vector128<int> r0, Vector128<int> r1, Vector128<int> r2, Vector128<int> r3)
    {
        var a = ZipLow(r0, r1);
        var b = ZipLow(r2, r3);
        var c = ZipHigh(r0, r1);
        var e = ZipHigh(r2, r3);
        return (
            ZipLow(a.AsUInt64(), b.AsUInt64()).AsInt32(),
            ZipHigh(a.AsUInt64(), b.AsUInt64()).AsInt32(),
            ZipLow(c.AsUInt64(), e.AsUInt64()).AsInt32(),
            ZipHigh(c.AsUInt64(), e.AsUInt64()).AsInt32());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> ZipLow(Vector128<int> left, Vector128<int> right) =>
        Sse2.IsSupported ? Sse2.UnpackLow(left, right) : AdvSimd.Arm64.ZipLow(left, right);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> ZipHigh(Vector128<int> left, Vector128<int> right) =>
        Sse2.IsSupported ? Sse2.UnpackHigh(left, right) : AdvSimd.Arm64.ZipHigh(left, right);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ulong> ZipLow(Vector128<ulong> left, Vector128<ulong> right) =>
        Sse2.IsSupported ? Sse2.UnpackLow(left, right) : AdvSimd.Arm64.ZipLow(left, right);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ulong> ZipHigh(Vector128<ulong> left, Vector128<ulong> right) =>
        Sse2.IsSupported ? Sse2.UnpackHigh(left, right) : AdvSimd.Arm64.ZipHigh(left, right);

    public static void Transpose8x8(Span<Vector128<int>> low, Span<Vector128<int>> high)
    {
        var (a0, a1, a2, a3) = Transpose4x4(low[0], low[1], low[2], low[3]);
        var (b0, b1, b2, b3) = Transpose4x4(low[4], low[5], low[6], low[7]);
        var (c0, c1, c2, c3) = Transpose4x4(high[0], high[1], high[2], high[3]);
        var (e0, e1, e2, e3) = Transpose4x4(high[4], high[5], high[6], high[7]);
        low[0] = a0;
        low[1] = a1;
        low[2] = a2;
        low[3] = a3;
        high[0] = b0;
        high[1] = b1;
        high[2] = b2;
        high[3] = b3;
        low[4] = c0;
        low[5] = c1;
        low[6] = c2;
        low[7] = c3;
        high[4] = e0;
        high[5] = e1;
        high[6] = e2;
        high[7] = e3;
    }
}
