using System.Numerics;
using System.Runtime.CompilerServices;

namespace EggEncoder.Codecs.H264;

internal sealed class CabacDecoder
{
    private const int MaximumOverreadBytes = 8;

    private readonly byte[] _states = new byte[CabacTables.ContextCount];
    private byte[] _data = [];
    private int _length;
    private int _next;
    private ulong _cache;
    private int _cacheBits;
    private uint _range;
    private uint _offset;

    public long BitPosition => ((long)_next * 8) - _cacheBits;

    public void InitializeContexts(SliceType type, int cabacInitIdc, int sliceQp)
    {
        var column = type == SliceType.I ? 0 : cabacInitIdc + 1;
        for (var context = 0; context < CabacTables.ContextCount; context++)
        {
            _states[context] = CabacTables.InitialState(context, column, sliceQp);
        }
    }

    public void Start(byte[] data, int length, long bitPosition)
    {
        if ((bitPosition & 7) != 0)
        {
            throw new InvalidOperationException("CABAC starts on a byte boundary");
        }

        _data = data;
        _length = length;
        _next = (int)(bitPosition >> 3);
        _cache = 0;
        _cacheBits = 0;
        _range = 510;
        _offset = ReadBits(9);
        if (_offset >= 510)
        {
            throw new InvalidDataException("H.264: the CABAC data of a slice starts with an invalid offset");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DecodeDecision(int context)
    {
        ref var state = ref _states[context];
        int current = state;
        uint lps = CabacTables.RangeLps[((current >> 1) << 2) + (int)((_range >> 6) & 3)];
        var range = _range - lps;
        var lpsMask = (uint)((int)(range - 1 - _offset) >> 31);
        var offset = _offset - (range & lpsMask);
        range = (range & ~lpsMask) | (lps & lpsMask);
        var bin = (current ^ (int)lpsMask) & 1;
        state = CabacTables.NextStateForBin[(current << 1) | bin];
        var shift = BitOperations.LeadingZeroCount(range) - 23;
        _range = range << shift;
        _offset = (offset << shift) | ReadBitsOrNone(shift);
        return bin;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DecodeBypass()
    {
        _offset = (_offset << 1) | ReadBits(1);
        if (_offset >= _range)
        {
            _offset -= _range;
            return 1;
        }

        return 0;
    }

    public int DecodeTerminate()
    {
        _range -= 2;
        if (_offset >= _range)
        {
            return 1;
        }

        if (_range < 256)
        {
            _range <<= 1;
            _offset = (_offset << 1) | ReadBits(1);
        }

        return 0;
    }

    public int DecodeExpGolombBypass(int k)
    {
        var value = 0;
        while (DecodeBypass() == 1)
        {
            value += 1 << k;
            k++;
            if (k > 24)
            {
                throw new InvalidDataException("H.264: a CABAC Exp-Golomb code is too long");
            }
        }

        while (k-- > 0)
        {
            value += DecodeBypass() << k;
        }

        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadBitsOrNone(int count)
    {
        if (_cacheBits < count)
        {
            Refill();
        }

        var value = (uint)((_cache >> (63 - count)) >> 1);
        _cache <<= count;
        _cacheBits -= count;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadBits(int count)
    {
        if (_cacheBits < count)
        {
            Refill();
        }

        var value = (uint)(_cache >> (64 - count));
        _cache <<= count;
        _cacheBits -= count;
        return value;
    }

    private void Refill()
    {
        if (_next > _length + MaximumOverreadBytes)
        {
            throw new InvalidDataException("H.264: the CABAC data of a slice ends too early");
        }

        while (_cacheBits <= 56)
        {
            ulong value = _next < _length ? _data[_next] : (byte)0;
            _cache |= value << (56 - _cacheBits);
            _cacheBits += 8;
            _next++;
        }
    }
}
