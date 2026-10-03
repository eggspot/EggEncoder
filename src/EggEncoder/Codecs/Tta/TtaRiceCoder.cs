using EggEncoder.Transform;

namespace EggEncoder.Codecs.Tta
{
    // TTA's adaptive two-tier Rice-like entropy coder for one channel's residual stream. Unlike ALAC's
    // Rice coder, this has no escape/verbatim fallback -- its unary code is unbounded (capped only by
    // the remaining bitstream length), so every residual is always representable; there is no
    // bps-dependent range to overflow. Confirmed bit-exact against ffmpeg's tta.c/ttaenc.c (the actual
    // decode_frame/encode_frame inline logic, not a named standalone function in ffmpeg's own source).
    //
    // Maintains two independent adaptive (k, sum) pairs: k0/sum0 always adapt; k1/sum1 only adapt (and
    // only exist in the bitstream at all) for a "large" value, signaled by at least one 1-bit in the
    // initial unary prefix. Resets fresh at the start of every frame (new instance per frame per
    // channel), never persisted across frames.
    internal sealed class TtaRiceCoder
    {
        private const int InitialK = 10;
        private static readonly uint[] Shift1 = BuildShift1Table();

        private uint _k0 = InitialK;
        private uint _k1 = InitialK;
        private uint _sum0;
        private uint _sum1;

        public TtaRiceCoder()
        {
            _sum0 = Shift16(_k0);
            _sum1 = Shift16(_k1);
        }

        public int DecodeResidual(BitReader reader)
        {
            var unary = (uint)ReadUnary(reader);
            var isLarge = unary != 0;
            uint k;

            if (isLarge)
            {
                k = _k1;
                unary -= 1;
            }
            else
            {
                k = _k0;
            }

            var value = k != 0 ? (unary << (int)k) + reader.ReadBits((int)k) : unary;

            if (isLarge)
            {
                _sum1 += value - (_sum1 >> 4);
                if (_k1 > 0 && _sum1 < Shift16(_k1))
                {
                    _k1--;
                }
                else if (_sum1 > Shift16(_k1 + 1))
                {
                    _k1++;
                }

                value += Shift1[_k0];
            }

            _sum0 += value - (_sum0 >> 4);
            if (_k0 > 0 && _sum0 < Shift16(_k0))
            {
                _k0--;
            }
            else if (_sum0 > Shift16(_k0 + 1))
            {
                _k0++;
            }

            var signedValue = (int)value;
            return 1 + ((signedValue >> 1) ^ ((signedValue & 1) - 1));
        }

        public void EncodeResidual(BitWriter writer, int residual)
        {
            var outval = (uint)(residual > 0 ? (residual << 1) - 1 : -residual << 1);

            var k = _k0;
            _sum0 += outval - (_sum0 >> 4);
            if (_k0 > 0 && _sum0 < Shift16(_k0))
            {
                _k0--;
            }
            else if (_sum0 > Shift16(_k0 + 1))
            {
                _k0++;
            }

            if (outval >= Shift1[k])
            {
                outval -= Shift1[k];
                k = _k1;
                _sum1 += outval - (_sum1 >> 4);
                if (_k1 > 0 && _sum1 < Shift16(_k1))
                {
                    _k1--;
                }
                else if (_sum1 > Shift16(_k1 + 1))
                {
                    _k1++;
                }

                var unary = 1 + (int)(outval >> (int)k);
                while (unary > 0)
                {
                    if (unary > 31)
                    {
                        writer.WriteBits(0x7FFFFFFF, 31);
                        unary -= 31;
                    }
                    else
                    {
                        writer.WriteBits((1u << unary) - 1u, unary);
                        unary = 0;
                    }
                }
            }

            writer.WriteBits(0, 1);
            if (k != 0)
            {
                writer.WriteBits(outval & (Shift1[k] - 1), (int)k);
            }
        }

        private static int ReadUnary(BitReader reader)
        {
            var count = 0;
            while (reader.ReadBits(1) != 0)
            {
                count++;
            }

            return count;
        }

        private static uint Shift16(uint k) => Shift1[k + 4];

        private static uint[] BuildShift1Table()
        {
            var table = new uint[41];
            for (var k = 0; k < 32; k++)
            {
                table[k] = 1u << k;
            }

            for (var k = 32; k < 40; k++)
            {
                table[k] = 0x80000000u;
            }

            table[40] = 0xFFFFFFFFu;
            return table;
        }
    }
}
