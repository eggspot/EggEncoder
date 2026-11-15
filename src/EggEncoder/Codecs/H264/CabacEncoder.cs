using System.Numerics;
using EggEncoder.Transform;

namespace EggEncoder.Codecs.H264;

internal sealed class CabacEncoder
{
    private const int WindowBits = 10;
    private const int NoHeldByte = -1;

    private readonly byte[] _states = new byte[CabacTables.ContextCount];
    private BitWriter _writer = new();
    private ulong _low;
    private int _range;
    private int _settledBits;
    private int _heldByte;
    private int _heldFfBytes;

    public void InitializeContexts(SliceType type, int cabacInitIdc, int sliceQp)
    {
        var column = type == SliceType.I ? 0 : cabacInitIdc + 1;
        for (var context = 0; context < CabacTables.ContextCount; context++)
        {
            _states[context] = CabacTables.InitialState(context, column, sliceQp);
        }
    }

    public void Start(BitWriter writer)
    {
        _writer = writer;
        _low = 0;
        _range = 510;
        _settledBits = -1;
        _heldByte = NoHeldByte;
        _heldFfBytes = 0;
    }

    public void EncodeDecision(int context, int bin)
    {
        ref var state = ref _states[context];
        int current = state;
        int lps = CabacTables.RangeLps[((current >> 1) << 2) + ((_range >> 6) & 3)];
        var range = _range - lps;
        if (bin != (current & 1))
        {
            _low += (ulong)range;
            range = lps;
            state = CabacTables.NextStateLps[current];
        }
        else
        {
            state = CabacTables.NextStateMps[current];
        }

        var shift = BitOperations.LeadingZeroCount((uint)range) - 23;
        _range = range << shift;
        _low <<= shift;
        _settledBits += shift;
        if (_settledBits >= 8)
        {
            EmitSettledBytes();
        }
    }

    public void EncodeBypass(int bin)
    {
        _low = (_low << 1) + (bin != 0 ? (ulong)_range : 0UL);
        if (++_settledBits >= 8)
        {
            EmitSettledBytes();
        }
    }

    public void EncodeTerminate(int bin)
    {
        var range = _range - 2;
        if (bin == 0)
        {
            var shift = BitOperations.LeadingZeroCount((uint)range) - 23;
            _range = range << shift;
            _low <<= shift;
            _settledBits += shift;
            if (_settledBits >= 8)
            {
                EmitSettledBytes();
            }

            return;
        }

        _low = (_low + (ulong)range) | 1;
        Finish(_settledBits + WindowBits);
    }

    public void EncodeExpGolombBypass(int value, int k)
    {
        while (value >= 1 << k)
        {
            EncodeBypass(1);
            value -= 1 << k;
            k++;
        }

        EncodeBypass(0);
        while (k-- > 0)
        {
            EncodeBypass((value >> k) & 1);
        }
    }

    private void EmitSettledBytes()
    {
        do
        {
            var position = WindowBits + _settledBits - 8;
            var chunk = (int)(_low >> position);
            _low &= (1UL << position) - 1;
            _settledBits -= 8;
            Settle(chunk);
        }
        while (_settledBits >= 8);
    }

    private void Settle(int chunk)
    {
        if (chunk == 0xFF)
        {
            _heldFfBytes++;
            return;
        }

        ReleaseHeld(chunk >> 8);
        _heldByte = chunk & 0xFF;
    }

    private void ReleaseHeld(int carry)
    {
        if (_heldByte != NoHeldByte)
        {
            _writer.WriteBits((uint)((_heldByte + carry) & 0xFF), 8);
        }

        var ff = (uint)((0xFF + carry) & 0xFF);
        for (; _heldFfBytes > 0; _heldFfBytes--)
        {
            _writer.WriteBits(ff, 8);
        }

        _heldByte = NoHeldByte;
    }

    private void Finish(int bits)
    {
        while (bits >= 8)
        {
            bits -= 8;
            var chunk = (int)(_low >> bits);
            _low &= (1UL << bits) - 1;
            Settle(chunk);
        }

        var carry = (int)(_low >> Math.Max(bits, 0));
        ReleaseHeld(carry);
        if (bits > 0)
        {
            _writer.WriteBits((uint)(_low & ((1UL << bits) - 1)), bits);
        }
    }
}
