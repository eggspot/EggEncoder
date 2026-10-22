using EggEncoder.Transform;

namespace EggEncoder.Media
{
    // Reads the raw byte sequence payload (RBSP) of one H.264/H.265 NAL unit: emulation-prevention
    // bytes (the 0x03 in every 00 00 03 sequence) are removed up front, then fields are read MSB
    // first via the shared Transform.BitReader, plus the Exp-Golomb codes both specs use
    // (H.264 clause 9.1, H.265 clause 9.2). Every overrun surfaces as InvalidDataException, so a
    // truncated or corrupted parameter set can never escape as EndOfStreamException.
    internal sealed class RbspReader
    {
        // ue(v) values are at most 2^32 - 2 in both specs, i.e. at most 31 leading zero bits.
        private const int MaxExpGolombLeadingZeros = 31;

        private readonly BitReader _reader;
        private readonly string _what;

        public RbspReader(ReadOnlySpan<byte> nalUnit, int headerLength, string what)
        {
            _what = what;
            _reader = new BitReader(RemoveEmulationPrevention(nalUnit[headerLength..]));
        }

        public uint ReadBits(int count)
        {
            try
            {
                return _reader.ReadBits(count);
            }
            catch (EndOfStreamException e)
            {
                throw Truncated(e);
            }
        }

        public bool ReadFlag() => ReadBits(1) == 1;

        public void SkipBits(int count)
        {
            try
            {
                _reader.SkipBits(count);
            }
            catch (EndOfStreamException e)
            {
                throw Truncated(e);
            }
        }

        public uint ReadUnsignedExpGolomb()
        {
            var leadingZeros = 0;
            while (!ReadFlag())
            {
                if (++leadingZeros > MaxExpGolombLeadingZeros)
                {
                    throw new InvalidDataException($"{_what}: Exp-Golomb code longer than 32 bits");
                }
            }

            return (uint)((1UL << leadingZeros) - 1 + ReadBits(leadingZeros));
        }

        public int ReadSignedExpGolomb()
        {
            var codeNum = ReadUnsignedExpGolomb();
            var magnitude = (long)((codeNum + 1UL) / 2);
            return (int)((codeNum & 1) == 1 ? magnitude : -magnitude);
        }

        // ue(v) with a spec-given upper bound, so a corrupted value can't drive a loop or an
        // allocation further.
        public int ReadUnsignedExpGolomb(uint max, string field)
        {
            var value = ReadUnsignedExpGolomb();
            if (value > max)
            {
                throw new InvalidDataException($"{_what}: {field} is {value}, above its maximum of {max}");
            }

            return (int)value;
        }

        private InvalidDataException Truncated(Exception inner) => new($"{_what} is truncated", inner);

        internal static byte[] RemoveEmulationPrevention(ReadOnlySpan<byte> data)
        {
            var output = new byte[data.Length];
            var length = 0;
            var zeros = 0;
            foreach (var b in data)
            {
                if (zeros >= 2 && b == 3)
                {
                    zeros = 0;
                    continue;
                }

                zeros = b == 0 ? zeros + 1 : 0;
                output[length++] = b;
            }

            return output.AsSpan(0, length).ToArray();
        }
    }
}
