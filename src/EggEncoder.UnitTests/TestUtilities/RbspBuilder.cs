using EggEncoder.Transform;

namespace EggEncoder.UnitTests.TestUtilities
{
    // Builds a synthetic H.264/H.265 NAL unit bit by bit (fixed-width fields plus Exp-Golomb
    // codes), then inserts emulation-prevention bytes the way a real encoder must, so parser
    // tests can reach SPS branches no checked-in fixture exercises (POC type 1, scaling lists,
    // monochrome, 4:4:4, sub-layers).
    internal sealed class RbspBuilder
    {
        private readonly BitWriter _writer = new();
        private readonly byte[] _nalHeader;

        public RbspBuilder(params byte[] nalHeader)
        {
            _nalHeader = nalHeader;
        }

        public RbspBuilder Bits(uint value, int count)
        {
            _writer.WriteBits(value, count);
            return this;
        }

        public RbspBuilder Flag(bool value) => Bits(value ? 1u : 0u, 1);

        public RbspBuilder Ue(uint value)
        {
            var codeNum = (ulong)value + 1;
            var leadingZeros = 63 - System.Numerics.BitOperations.LeadingZeroCount(codeNum);
            _writer.WriteBits(0, leadingZeros);
            if (leadingZeros + 1 > 32)
            {
                _writer.WriteBits((uint)(codeNum >> 32), leadingZeros + 1 - 32);
                _writer.WriteBits((uint)codeNum, 32);
            }
            else
            {
                _writer.WriteBits((uint)codeNum, leadingZeros + 1);
            }

            return this;
        }

        public RbspBuilder Se(int value) => Ue(value > 0 ? (uint)((2L * value) - 1) : (uint)(-2L * value));

        // rbsp_trailing_bits, then the NAL header and emulation prevention.
        public byte[] Build()
        {
            _writer.WriteBits(1, 1);
            _writer.ByteAlign();
            return [.. _nalHeader, .. InsertEmulationPrevention(_writer.ToArray())];
        }

        // Raw payload with no trailing bits (for truncation tests).
        public byte[] BuildWithoutTrailingBits()
        {
            _writer.ByteAlign();
            return [.. _nalHeader, .. InsertEmulationPrevention(_writer.ToArray())];
        }

        private static byte[] InsertEmulationPrevention(byte[] rbsp)
        {
            var output = new List<byte>(rbsp.Length + 4);
            var zeros = 0;
            foreach (var b in rbsp)
            {
                if (zeros >= 2 && b <= 3)
                {
                    output.Add(3);
                    zeros = 0;
                }

                output.Add(b);
                zeros = b == 0 ? zeros + 1 : 0;
            }

            return [.. output];
        }
    }
}
