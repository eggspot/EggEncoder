using EggEncoder.Codecs.H264;
using EggEncoder.Transform;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.H264
{
    public class CabacEncoderTest
    {
        [Theory]
        [InlineData(1, 95)]
        [InlineData(2, 60)]
        [InlineData(3, 99)]
        [InlineData(4, 50)]
        public void Output_Should_Equal_The_Bit_Serial_Encoding_Process_Of_The_Standard(int seed, int likelyPercent)
        {
            var random = new Random(seed);
            var operations = new List<(int Kind, int Context, int Bin)>();
            for (var i = 0; i < 40000; i++)
            {
                var kind = random.Next(20) switch
                {
                    < 15 => 0,
                    < 19 => 1,
                    _ => 2
                };
                var context = random.Next(CabacTables.ContextCount - 1);
                if (context == 276)
                {
                    context = 0;
                }

                var bin = kind == 2 ? 0 : random.Next(100) < likelyPercent ? context & 1 : 1 - (context & 1);
                operations.Add((kind, context, bin));
            }

            var fast = Encode(new FastEngine(), operations);
            var reference = Encode(new StandardEngine(), operations);

            fast.Should().Equal(reference);
        }

        [Fact]
        public void A_Slice_With_Only_The_End_Of_Slice_Bin_Should_Match_The_Standard()
        {
            List<(int Kind, int Context, int Bin)> operations = [];

            Encode(new FastEngine(), operations).Should().Equal(Encode(new StandardEngine(), operations));
        }

        private static byte[] Encode(IEngine engine, List<(int Kind, int Context, int Bin)> operations)
        {
            var writer = new BitWriter();
            writer.WriteBits(0b101, 3);
            writer.ByteAlign();
            engine.Start(writer);
            foreach (var (kind, context, bin) in operations)
            {
                switch (kind)
                {
                    case 0:
                        engine.Decision(context, bin);
                        break;
                    case 1:
                        engine.Bypass(bin);
                        break;
                    default:
                        engine.Terminate(0);
                        break;
                }
            }

            engine.Terminate(1);
            writer.ByteAlign();
            return writer.ToArray();
        }

        private interface IEngine
        {
            void Start(BitWriter writer);

            void Decision(int context, int bin);

            void Bypass(int bin);

            void Terminate(int bin);
        }

        private sealed class FastEngine : IEngine
        {
            private readonly CabacEncoder _encoder = new();

            public void Start(BitWriter writer)
            {
                _encoder.InitializeContexts(SliceType.P, 1, 30);
                _encoder.Start(writer);
            }

            public void Decision(int context, int bin) => _encoder.EncodeDecision(context, bin);

            public void Bypass(int bin) => _encoder.EncodeBypass(bin);

            public void Terminate(int bin) => _encoder.EncodeTerminate(bin);
        }

        private sealed class StandardEngine : IEngine
        {
            private readonly byte[] _states = new byte[CabacTables.ContextCount];
            private BitWriter _writer = new();
            private int _low;
            private int _range;
            private int _outstanding;
            private bool _firstBit;

            public void Start(BitWriter writer)
            {
                for (var context = 0; context < CabacTables.ContextCount; context++)
                {
                    _states[context] = CabacTables.InitialState(context, 2, 30);
                }

                _writer = writer;
                _low = 0;
                _range = 510;
                _outstanding = 0;
                _firstBit = true;
            }

            public void Decision(int context, int bin)
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

            public void Bypass(int bin)
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

            public void Terminate(int bin)
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
}
