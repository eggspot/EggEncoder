using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace EggEncoder.Codecs.Mp3;

[SkipLocalsInit]
internal static class Mp3Imdct
{
    public static void Long(ReadOnlySpan<float> input, Span<float> output, float[] window)
    {
        ref var column = ref MemoryMarshal.GetArrayDataReference(Mp3DecoderTables.LongImdctColumns);
        var a0 = Vector128<float>.Zero;
        var a1 = Vector128<float>.Zero;
        var a2 = Vector128<float>.Zero;
        var a3 = Vector128<float>.Zero;
        var a4 = Vector128<float>.Zero;
        for (var k = 0; k < 18; k++)
        {
            var scale = Vector128.Create(input[k]);
            ref var c = ref Unsafe.Add(ref column, k * 20);
            a0 = Mp3Vector.MultiplyAdd(Vector128.LoadUnsafe(ref c), scale, a0);
            a1 = Mp3Vector.MultiplyAdd(Vector128.LoadUnsafe(ref c, 4), scale, a1);
            a2 = Mp3Vector.MultiplyAdd(Vector128.LoadUnsafe(ref c, 8), scale, a2);
            a3 = Mp3Vector.MultiplyAdd(Vector128.LoadUnsafe(ref c, 12), scale, a3);
            a4 = Mp3Vector.MultiplyAdd(Vector128.LoadUnsafe(ref c, 16), scale, a4);
        }

        Span<float> raw = stackalloc float[20];
        ref var r = ref MemoryMarshal.GetReference(raw);
        a0.StoreUnsafe(ref r);
        a1.StoreUnsafe(ref r, 4);
        a2.StoreUnsafe(ref r, 8);
        a3.StoreUnsafe(ref r, 12);
        a4.StoreUnsafe(ref r, 16);
        for (var i = 0; i < 9; i++)
        {
            var front = raw[i];
            var back = raw[10 + i];
            output[i] = front * window[i];
            output[17 - i] = -front * window[17 - i];
            output[18 + i] = back * window[18 + i];
            output[35 - i] = back * window[35 - i];
        }
    }

    public static void LongWithOverlap(float[] spectrum, int offset, float[] output, int subband, float[] overlap, float[] window, bool invertOdd)
    {
        ref var column = ref MemoryMarshal.GetArrayDataReference(Mp3DecoderTables.LongImdctColumns);
        ref var x = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(spectrum), offset);
        Span<float> raw = stackalloc float[20];
        ref var r = ref MemoryMarshal.GetReference(raw);
        if (Vector256.IsHardwareAccelerated)
        {
            var w0 = Vector256<float>.Zero;
            var w1 = Vector256<float>.Zero;
            var n2 = Vector128<float>.Zero;
            var u0 = Vector256<float>.Zero;
            var u1 = Vector256<float>.Zero;
            var m2 = Vector128<float>.Zero;
            for (var k = 0; k < 18; k += 2)
            {
                var even = Unsafe.Add(ref x, k);
                var odd = Unsafe.Add(ref x, k + 1);
                ref var c = ref Unsafe.Add(ref column, k * 20);
                var wideEven = Vector256.Create(even);
                var wideOdd = Vector256.Create(odd);
                w0 = Mp3Vector.MultiplyAdd(Vector256.LoadUnsafe(ref c), wideEven, w0);
                w1 = Mp3Vector.MultiplyAdd(Vector256.LoadUnsafe(ref c, 8), wideEven, w1);
                n2 = Mp3Vector.MultiplyAdd(Vector128.LoadUnsafe(ref c, 16), Vector128.Create(even), n2);
                u0 = Mp3Vector.MultiplyAdd(Vector256.LoadUnsafe(ref c, 20), wideOdd, u0);
                u1 = Mp3Vector.MultiplyAdd(Vector256.LoadUnsafe(ref c, 28), wideOdd, u1);
                m2 = Mp3Vector.MultiplyAdd(Vector128.LoadUnsafe(ref c, 36), Vector128.Create(odd), m2);
            }

            (w0 + u0).StoreUnsafe(ref r);
            (w1 + u1).StoreUnsafe(ref r, 8);
            (n2 + m2).StoreUnsafe(ref r, 16);
        }
        else
        {
            var a0 = Vector128<float>.Zero;
            var a1 = Vector128<float>.Zero;
            var a2 = Vector128<float>.Zero;
            var a3 = Vector128<float>.Zero;
            var a4 = Vector128<float>.Zero;
            for (var k = 0; k < 18; k++)
            {
                var scale = Vector128.Create(Unsafe.Add(ref x, k));
                ref var c = ref Unsafe.Add(ref column, k * 20);
                a0 = Mp3Vector.MultiplyAdd(Vector128.LoadUnsafe(ref c), scale, a0);
                a1 = Mp3Vector.MultiplyAdd(Vector128.LoadUnsafe(ref c, 4), scale, a1);
                a2 = Mp3Vector.MultiplyAdd(Vector128.LoadUnsafe(ref c, 8), scale, a2);
                a3 = Mp3Vector.MultiplyAdd(Vector128.LoadUnsafe(ref c, 12), scale, a3);
                a4 = Mp3Vector.MultiplyAdd(Vector128.LoadUnsafe(ref c, 16), scale, a4);
            }

            a0.StoreUnsafe(ref r);
            a1.StoreUnsafe(ref r, 4);
            a2.StoreUnsafe(ref r, 8);
            a3.StoreUnsafe(ref r, 12);
            a4.StoreUnsafe(ref r, 16);
        }

        ref var w = ref MemoryMarshal.GetArrayDataReference(window);
        ref var o = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(output), subband);
        ref var p = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(overlap), offset);
        var oddSign = invertOdd ? -1f : 1f;
        for (var i = 0; i < 9; i++)
        {
            var front = Unsafe.Add(ref r, i);
            var back = Unsafe.Add(ref r, 10 + i);
            var low = (front * Unsafe.Add(ref w, i)) + Unsafe.Add(ref p, i);
            var high = (-front * Unsafe.Add(ref w, 17 - i)) + Unsafe.Add(ref p, 17 - i);
            Unsafe.Add(ref o, i * 32) = (i & 1) == 1 ? low * oddSign : low;
            Unsafe.Add(ref o, (17 - i) * 32) = (i & 1) == 0 ? high * oddSign : high;
            Unsafe.Add(ref p, i) = back * Unsafe.Add(ref w, 18 + i);
            Unsafe.Add(ref p, 17 - i) = back * Unsafe.Add(ref w, 35 - i);
        }
    }

    public static void Short(ReadOnlySpan<float> interleaved, Span<float> output)
    {
        output.Clear();
        var columns = Mp3DecoderTables.ShortImdctColumns;
        var window = Mp3DecoderTables.ShortWindow;
        Span<float> block = stackalloc float[12];
        for (var w = 0; w < 3; w++)
        {
            block.Clear();
            for (var k = 0; k < 6; k++)
            {
                var x = interleaved[(3 * k) + w];
                if (x == 0)
                {
                    continue;
                }

                var column = columns.AsSpan(k * 12, 12);
                for (var i = 0; i < 12; i++)
                {
                    block[i] += x * column[i];
                }
            }

            var offset = 6 + (6 * w);
            for (var i = 0; i < 12; i++)
            {
                output[offset + i] += block[i] * window[i];
            }
        }
    }
}

[SkipLocalsInit]
internal sealed class Mp3Synthesis
{
    private const int BufferLength = 1024;

    private readonly float[] _v = new float[BufferLength * 2];
    private int _offset;

    public void Reset()
    {
        Array.Clear(_v);
        _offset = 0;
    }

    public void Synthesize(float[] subbands, Span<float> pcm)
    {
        for (var time = 0; time < 18; time++)
        {
            _offset = (_offset - 64) & (BufferLength - 1);
            Matrix(subbands.AsSpan(time * 32, 32));
            Window(pcm.Slice(time * 32, 32));
        }
    }

    private void Matrix(ReadOnlySpan<float> input)
    {
        ref var column = ref MemoryMarshal.GetArrayDataReference(Mp3DecoderTables.SynthesisColumns);
        ref var first = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_v), _offset);
        ref var second = ref Unsafe.Add(ref first, BufferLength);
        if (Vector256.IsHardwareAccelerated)
        {
            ref var dct = ref MemoryMarshal.GetArrayDataReference(Mp3DecoderTables.SynthesisDctColumns);
            var b0 = Vector256<float>.Zero;
            var b1 = Vector256<float>.Zero;
            var b2 = Vector256<float>.Zero;
            var b3 = Vector256<float>.Zero;
            var e0 = Vector256<float>.Zero;
            var e1 = Vector256<float>.Zero;
            var e2 = Vector256<float>.Zero;
            var e3 = Vector256<float>.Zero;
            ref var source = ref MemoryMarshal.GetReference(input);
            for (var k = 0; k < 32; k += 2)
            {
                var even = Vector256.Create(Unsafe.Add(ref source, k));
                var odd = Vector256.Create(Unsafe.Add(ref source, k + 1));
                ref var c = ref Unsafe.Add(ref dct, k * 32);
                b0 = Mp3Vector.MultiplyAdd(Vector256.LoadUnsafe(ref c), even, b0);
                b1 = Mp3Vector.MultiplyAdd(Vector256.LoadUnsafe(ref c, 8), even, b1);
                b2 = Mp3Vector.MultiplyAdd(Vector256.LoadUnsafe(ref c, 16), even, b2);
                b3 = Mp3Vector.MultiplyAdd(Vector256.LoadUnsafe(ref c, 24), even, b3);
                e0 = Mp3Vector.MultiplyAdd(Vector256.LoadUnsafe(ref c, 32), odd, e0);
                e1 = Mp3Vector.MultiplyAdd(Vector256.LoadUnsafe(ref c, 40), odd, e1);
                e2 = Mp3Vector.MultiplyAdd(Vector256.LoadUnsafe(ref c, 48), odd, e2);
                e3 = Mp3Vector.MultiplyAdd(Vector256.LoadUnsafe(ref c, 56), odd, e3);
            }

            b0 += e0;
            b1 += e1;
            b2 += e2;
            b3 += e3;

            Span<float> x = stackalloc float[40];
            ref var xs = ref MemoryMarshal.GetReference(x);
            b0.StoreUnsafe(ref xs);
            b1.StoreUnsafe(ref xs, 8);
            b2.StoreUnsafe(ref xs, 16);
            b3.StoreUnsafe(ref xs, 24);
            Vector256<float>.Zero.StoreUnsafe(ref xs, 32);
            var reverse = Vector256.Create(7, 6, 5, 4, 3, 2, 1, 0);
            var v2 = -Vector256.Shuffle(Vector256.LoadUnsafe(ref xs, 25), reverse);
            var v3 = -Vector256.Shuffle(Vector256.LoadUnsafe(ref xs, 17), reverse);
            var v4 = -Vector256.Shuffle(Vector256.LoadUnsafe(ref xs, 9), reverse);
            var v5 = -Vector256.Shuffle(Vector256.LoadUnsafe(ref xs, 1), reverse);
            var v6 = -b0;
            var v7 = -b1;
            b2.StoreUnsafe(ref first);
            b3.StoreUnsafe(ref first, 8);
            v2.StoreUnsafe(ref first, 16);
            v3.StoreUnsafe(ref first, 24);
            v4.StoreUnsafe(ref first, 32);
            v5.StoreUnsafe(ref first, 40);
            v6.StoreUnsafe(ref first, 48);
            v7.StoreUnsafe(ref first, 56);
            b2.StoreUnsafe(ref second);
            b3.StoreUnsafe(ref second, 8);
            v2.StoreUnsafe(ref second, 16);
            v3.StoreUnsafe(ref second, 24);
            v4.StoreUnsafe(ref second, 32);
            v5.StoreUnsafe(ref second, 40);
            v6.StoreUnsafe(ref second, 48);
            v7.StoreUnsafe(ref second, 56);
            return;
        }

        for (var part = 0; part < 64; part += 16)
        {
            var a0 = Vector128<float>.Zero;
            var a1 = Vector128<float>.Zero;
            var a2 = Vector128<float>.Zero;
            var a3 = Vector128<float>.Zero;
            for (var k = 0; k < 32; k++)
            {
                var value = input[k];
                if (value == 0)
                {
                    continue;
                }

                var scale = Vector128.Create(value);
                ref var c = ref Unsafe.Add(ref column, (k * 64) + part);
                a0 = Mp3Vector.MultiplyAdd(Vector128.LoadUnsafe(ref c), scale, a0);
                a1 = Mp3Vector.MultiplyAdd(Vector128.LoadUnsafe(ref c, 4), scale, a1);
                a2 = Mp3Vector.MultiplyAdd(Vector128.LoadUnsafe(ref c, 8), scale, a2);
                a3 = Mp3Vector.MultiplyAdd(Vector128.LoadUnsafe(ref c, 12), scale, a3);
            }

            ref var target = ref Unsafe.Add(ref first, part);
            ref var copy = ref Unsafe.Add(ref second, part);
            a0.StoreUnsafe(ref target);
            a1.StoreUnsafe(ref target, 4);
            a2.StoreUnsafe(ref target, 8);
            a3.StoreUnsafe(ref target, 12);
            a0.StoreUnsafe(ref copy);
            a1.StoreUnsafe(ref copy, 4);
            a2.StoreUnsafe(ref copy, 8);
            a3.StoreUnsafe(ref copy, 12);
        }
    }

    private void Window(Span<float> pcm)
    {
        ref var d = ref MemoryMarshal.GetArrayDataReference(Mp3DecoderTables.SynthesisWindow);
        ref var v = ref MemoryMarshal.GetArrayDataReference(_v);
        ref var o = ref MemoryMarshal.GetReference(pcm);
        if (Vector256.IsHardwareAccelerated)
        {
            var s0 = Vector256<float>.Zero;
            var s1 = Vector256<float>.Zero;
            var s2 = Vector256<float>.Zero;
            var s3 = Vector256<float>.Zero;
            var t0 = Vector256<float>.Zero;
            var t1 = Vector256<float>.Zero;
            var t2 = Vector256<float>.Zero;
            var t3 = Vector256<float>.Zero;
            for (var i = 0; i < 8; i++)
            {
                ref var row = ref Unsafe.Add(ref v, _offset + (128 * i));
                ref var taps = ref Unsafe.Add(ref d, 64 * i);
                s0 = Mp3Vector.MultiplyAdd(Vector256.LoadUnsafe(ref row), Vector256.LoadUnsafe(ref taps), s0);
                s1 = Mp3Vector.MultiplyAdd(Vector256.LoadUnsafe(ref row, 8), Vector256.LoadUnsafe(ref taps, 8), s1);
                s2 = Mp3Vector.MultiplyAdd(Vector256.LoadUnsafe(ref row, 16), Vector256.LoadUnsafe(ref taps, 16), s2);
                s3 = Mp3Vector.MultiplyAdd(Vector256.LoadUnsafe(ref row, 24), Vector256.LoadUnsafe(ref taps, 24), s3);
                t0 = Mp3Vector.MultiplyAdd(Vector256.LoadUnsafe(ref row, 96), Vector256.LoadUnsafe(ref taps, 32), t0);
                t1 = Mp3Vector.MultiplyAdd(Vector256.LoadUnsafe(ref row, 104), Vector256.LoadUnsafe(ref taps, 40), t1);
                t2 = Mp3Vector.MultiplyAdd(Vector256.LoadUnsafe(ref row, 112), Vector256.LoadUnsafe(ref taps, 48), t2);
                t3 = Mp3Vector.MultiplyAdd(Vector256.LoadUnsafe(ref row, 120), Vector256.LoadUnsafe(ref taps, 56), t3);
            }

            (s0 + t0).StoreUnsafe(ref o);
            (s1 + t1).StoreUnsafe(ref o, 8);
            (s2 + t2).StoreUnsafe(ref o, 16);
            (s3 + t3).StoreUnsafe(ref o, 24);
            return;
        }

        for (var j = 0; j < 32; j += 4)
        {
            var sum = Vector128<float>.Zero;
            for (var i = 0; i < 8; i++)
            {
                var first = (nuint)(_offset + (128 * i) + j);
                var tap = (nuint)((64 * i) + j);
                sum = Mp3Vector.MultiplyAdd(Vector128.LoadUnsafe(ref v, first), Vector128.LoadUnsafe(ref d, tap), sum);
                sum = Mp3Vector.MultiplyAdd(Vector128.LoadUnsafe(ref v, first + 96), Vector128.LoadUnsafe(ref d, tap + 32), sum);
            }

            sum.StoreUnsafe(ref o, (nuint)j);
        }
    }
}

internal static class Mp3Vector
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> MultiplyAdd(Vector128<float> left, Vector128<float> right, Vector128<float> addend)
    {
        if (Fma.IsSupported)
        {
            return Fma.MultiplyAdd(left, right, addend);
        }

        if (AdvSimd.IsSupported)
        {
            return AdvSimd.FusedMultiplyAdd(addend, left, right);
        }

        return (left * right) + addend;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<float> MultiplyAdd(Vector256<float> left, Vector256<float> right, Vector256<float> addend) =>
        Fma.IsSupported ? Fma.MultiplyAdd(left, right, addend) : (left * right) + addend;
}
