using EggEncoder.Codecs.H264;
using EggEncoder.Transform;

namespace EggEncoder.UnitTests.Codecs.H264
{
    internal sealed class CabacTestEncoder
    {
        private readonly BitWriter _writer;
        private readonly int[] _probability = new int[CabacTables.ContextCount];
        private readonly int[] _mostProbable = new int[CabacTables.ContextCount];
        private int _low;
        private int _range;
        private int _outstanding;
        private bool _firstBit;

        public CabacTestEncoder(BitWriter writer, int column, int sliceQp)
        {
            _writer = writer;
            for (var context = 0; context < CabacTables.ContextCount; context++)
            {
                int m = CabacTables.ContextInit[(context * 8) + (column * 2)];
                int n = CabacTables.ContextInit[(context * 8) + (column * 2) + 1];
                var state = Math.Clamp(((m * Math.Clamp(sliceQp, 0, 51)) >> 4) + n, 1, 126);
                _probability[context] = state <= 63 ? 63 - state : state - 64;
                _mostProbable[context] = state <= 63 ? 0 : 1;
            }

            Restart();
        }

        public void Restart()
        {
            _low = 0;
            _range = 510;
            _outstanding = 0;
            _firstBit = true;
        }

        public void EncodeDecision(int context, int bin)
        {
            var probability = _probability[context];
            var lps = CabacTables.RangeLps[(probability * 4) + ((_range >> 6) & 3)];
            _range -= lps;
            if (bin != _mostProbable[context])
            {
                _low += _range;
                _range = lps;
                if (probability == 0)
                {
                    _mostProbable[context] = 1 - _mostProbable[context];
                }

                _probability[context] = CabacTables.NextStateLps[probability << 1] >> 1;
            }
            else
            {
                _probability[context] = CabacTables.NextStateMps[probability << 1] >> 1;
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
}
