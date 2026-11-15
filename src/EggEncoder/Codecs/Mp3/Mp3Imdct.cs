using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

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
            a0 += Vector128.LoadUnsafe(ref c) * scale;
            a1 += Vector128.LoadUnsafe(ref c, 4) * scale;
            a2 += Vector128.LoadUnsafe(ref c, 8) * scale;
            a3 += Vector128.LoadUnsafe(ref c, 12) * scale;
            a4 += Vector128.LoadUnsafe(ref c, 16) * scale;
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
        Span<float> x = stackalloc float[32];
        Span<float> slot = stackalloc float[32];
        for (var time = 0; time < 18; time++)
        {
            for (var k = 0; k < 32; k++)
            {
                slot[k] = subbands[(k * 18) + time];
            }

            Dct32(slot, x);
            _offset = (_offset - 64) & (BufferLength - 1);
            WriteV(x);
            Window(pcm.Slice(time * 32, 32));
        }
    }

    private static void Dct32(ReadOnlySpan<float> input, Span<float> output)
    {
        ref var column = ref MemoryMarshal.GetArrayDataReference(Mp3DecoderTables.SynthesisColumns);
        ref var o = ref MemoryMarshal.GetReference(output);
        if (Vector256.IsHardwareAccelerated)
        {
            var b0 = Vector256<float>.Zero;
            var b1 = Vector256<float>.Zero;
            var b2 = Vector256<float>.Zero;
            var b3 = Vector256<float>.Zero;
            for (var k = 0; k < 32; k++)
            {
                var value = input[k];
                if (value == 0)
                {
                    continue;
                }

                var scale = Vector256.Create(value);
                ref var c = ref Unsafe.Add(ref column, k * 32);
                b0 += Vector256.LoadUnsafe(ref c) * scale;
                b1 += Vector256.LoadUnsafe(ref c, 8) * scale;
                b2 += Vector256.LoadUnsafe(ref c, 16) * scale;
                b3 += Vector256.LoadUnsafe(ref c, 24) * scale;
            }

            b0.StoreUnsafe(ref o);
            b1.StoreUnsafe(ref o, 8);
            b2.StoreUnsafe(ref o, 16);
            b3.StoreUnsafe(ref o, 24);
            return;
        }

        var a0 = Vector128<float>.Zero;
        var a1 = Vector128<float>.Zero;
        var a2 = Vector128<float>.Zero;
        var a3 = Vector128<float>.Zero;
        var a4 = Vector128<float>.Zero;
        var a5 = Vector128<float>.Zero;
        var a6 = Vector128<float>.Zero;
        var a7 = Vector128<float>.Zero;
        for (var k = 0; k < 32; k++)
        {
            var value = input[k];
            if (value == 0)
            {
                continue;
            }

            var scale = Vector128.Create(value);
            ref var c = ref Unsafe.Add(ref column, k * 32);
            a0 += Vector128.LoadUnsafe(ref c) * scale;
            a1 += Vector128.LoadUnsafe(ref c, 4) * scale;
            a2 += Vector128.LoadUnsafe(ref c, 8) * scale;
            a3 += Vector128.LoadUnsafe(ref c, 12) * scale;
            a4 += Vector128.LoadUnsafe(ref c, 16) * scale;
            a5 += Vector128.LoadUnsafe(ref c, 20) * scale;
            a6 += Vector128.LoadUnsafe(ref c, 24) * scale;
            a7 += Vector128.LoadUnsafe(ref c, 28) * scale;
        }

        a0.StoreUnsafe(ref o);
        a1.StoreUnsafe(ref o, 4);
        a2.StoreUnsafe(ref o, 8);
        a3.StoreUnsafe(ref o, 12);
        a4.StoreUnsafe(ref o, 16);
        a5.StoreUnsafe(ref o, 20);
        a6.StoreUnsafe(ref o, 24);
        a7.StoreUnsafe(ref o, 28);
    }

    private void WriteV(ReadOnlySpan<float> x)
    {
        Span<float> v = stackalloc float[64];
        for (var i = 0; i < 16; i++)
        {
            v[i] = x[i + 16];
        }

        v[16] = 0;
        for (var i = 17; i < 48; i++)
        {
            v[i] = -x[48 - i];
        }

        v[48] = -x[0];
        for (var i = 49; i < 64; i++)
        {
            v[i] = -x[i - 48];
        }

        v.CopyTo(_v.AsSpan(_offset, 64));
        v.CopyTo(_v.AsSpan(_offset + BufferLength, 64));
    }

    private void Window(Span<float> pcm)
    {
        ref var d = ref MemoryMarshal.GetArrayDataReference(Mp3DecoderTables.SynthesisWindow);
        ref var v = ref MemoryMarshal.GetArrayDataReference(_v);
        ref var o = ref MemoryMarshal.GetReference(pcm);
        if (Vector256.IsHardwareAccelerated)
        {
            for (var j = 0; j < 32; j += 8)
            {
                var sum = Vector256<float>.Zero;
                for (var i = 0; i < 8; i++)
                {
                    var first = (nuint)(_offset + (128 * i) + j);
                    var tap = (nuint)((64 * i) + j);
                    sum += Vector256.LoadUnsafe(ref v, first) * Vector256.LoadUnsafe(ref d, tap);
                    sum += Vector256.LoadUnsafe(ref v, first + 96) * Vector256.LoadUnsafe(ref d, tap + 32);
                }

                sum.StoreUnsafe(ref o, (nuint)j);
            }

            return;
        }

        for (var j = 0; j < 32; j += 4)
        {
            var sum = Vector128<float>.Zero;
            for (var i = 0; i < 8; i++)
            {
                var first = (nuint)(_offset + (128 * i) + j);
                var tap = (nuint)((64 * i) + j);
                sum += Vector128.LoadUnsafe(ref v, first) * Vector128.LoadUnsafe(ref d, tap);
                sum += Vector128.LoadUnsafe(ref v, first + 96) * Vector128.LoadUnsafe(ref d, tap + 32);
            }

            sum.StoreUnsafe(ref o, (nuint)j);
        }
    }
}
