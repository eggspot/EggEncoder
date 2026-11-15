using EggEncoder.Transform;

namespace EggEncoder.Codecs.H264;

internal sealed class CabacEncoder
{
    private readonly byte[] _states = new byte[CabacTables.ContextCount];
    private BitWriter _writer = new();
    private int _low;
    private int _range;
    private int _outstanding;
    private bool _firstBit;

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
        _outstanding = 0;
        _firstBit = true;
    }

    public void EncodeDecision(int context, int bin)
    {
        var state = _states[context];
        int lps = CabacTables.RangeLps[((state >> 1) << 2) + ((_range >> 6) & 3)];
        _range -= lps;
        if (bin != (state & 1))
        {
            _low += _range;
            _range = lps;
            _states[context] = CabacTables.NextStateLps[state];
        }
        else
        {
            _states[context] = CabacTables.NextStateMps[state];
        }

        Renormalize();
    }

    public void EncodeBypass(int bin)
    {
        _low <<= 1;
        if (bin != 0)
        {
            _low += _range;
        }

        if (_low >= 1024)
        {
            PutBit(1);
            _low -= 1024;
        }
        else if (_low < 512)
        {
            PutBit(0);
        }
        else
        {
            _low -= 512;
            _outstanding++;
        }
    }

    public void EncodeTerminate(int bin)
    {
        _range -= 2;
        if (bin != 0)
        {
            _low += _range;
            _range = 2;
            Renormalize();
            PutBit((_low >> 9) & 1);
            _writer.WriteBits((uint)(((_low >> 7) & 3) | 1), 2);
        }
        else
        {
            Renormalize();
        }
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

    private void Renormalize()
    {
        while (_range < 256)
        {
            if (_low < 256)
            {
                PutBit(0);
            }
            else if (_low >= 512)
            {
                _low -= 512;
                PutBit(1);
            }
            else
            {
                _low -= 256;
                _outstanding++;
            }

            _range <<= 1;
            _low <<= 1;
        }
    }

    private void PutBit(int bit)
    {
        if (_firstBit)
        {
            _firstBit = false;
        }
        else
        {
            _writer.WriteBits((uint)bit, 1);
        }

        while (_outstanding > 0)
        {
            _writer.WriteBits((uint)(1 - bit), 1);
            _outstanding--;
        }
    }
}
