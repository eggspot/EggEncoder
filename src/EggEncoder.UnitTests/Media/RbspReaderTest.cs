using EggEncoder.Media;
using FluentAssertions;

namespace EggEncoder.UnitTests.Media
{
    public class RbspReaderTest
    {
        [Theory]
        [InlineData(new byte[] { 0, 0, 3, 1 }, new byte[] { 0, 0, 1 })]
        [InlineData(new byte[] { 0, 0, 3, 0, 0, 3 }, new byte[] { 0, 0, 0, 0 })]
        [InlineData(new byte[] { 0, 3, 0, 3 }, new byte[] { 0, 3, 0, 3 })]
        [InlineData(new byte[] { 0, 0, 0, 3 }, new byte[] { 0, 0, 0 })]
        [InlineData(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3 })]
        [InlineData(new byte[0], new byte[0])]
        public void RemoveEmulationPrevention_Should_Drop_The_03_After_Two_Zeros(byte[] input, byte[] expected)
        {
            RbspReader.RemoveEmulationPrevention(input).Should().Equal(expected);
        }

        [Fact]
        public void ReadExpGolomb_Should_Decode_Unsigned_And_Signed_Codes()
        {
            // ue: 1 -> 0, 010 -> 1, 011 -> 2, 00100 -> 3; se: 010 -> +1, 011 -> -1, 1 -> 0.
            var reader = new RbspReader([0xFF, 0b1010_0110, 0b0100_0100, 0b1110_0000], headerLength: 1, "test");

            reader.ReadUnsignedExpGolomb().Should().Be(0);
            reader.ReadUnsignedExpGolomb().Should().Be(1);
            reader.ReadUnsignedExpGolomb().Should().Be(2);
            reader.ReadUnsignedExpGolomb().Should().Be(3);
            reader.ReadSignedExpGolomb().Should().Be(1);
            reader.ReadSignedExpGolomb().Should().Be(-1);
            reader.ReadSignedExpGolomb().Should().Be(0);
        }

        [Fact]
        public void ReadUnsignedExpGolomb_MaximumValue_Should_Decode_Without_Overflow()
        {
            // 31 zeros, then 32 bits of 1: 2^32 - 2... encoded as codeNum + 1 = 0xFFFFFFFF in 32 bits.
            var bits = new byte[9];
            bits[3] = 0b0000_0001;
            bits.AsSpan(4, 4).Fill(0xFF);
            bits[8] = 0b1000_0000;

            var reader = new RbspReader([0x00, .. bits[1..]], headerLength: 0, "test");

            reader.ReadUnsignedExpGolomb().Should().Be(uint.MaxValue - 1);
        }

        [Fact]
        public void ReadSignedExpGolomb_LargestMagnitude_Should_Not_Overflow()
        {
            // codeNum 2^32 - 2 is even: se = -(2^31 - 1).
            var reader = new RbspReader([0, 0, 0, 1, 0xFF, 0xFF, 0xFF, 0xFE], headerLength: 0, "test");

            reader.ReadSignedExpGolomb().Should().Be(-int.MaxValue);
        }

        [Fact]
        public void Reads_PastTheEnd_Should_Throw_InvalidDataException()
        {
            var bits = () => new RbspReader([0xFF], headerLength: 0, "test").ReadBits(9);
            var skip = () => new RbspReader([0xFF], headerLength: 0, "test").SkipBits(9);

            bits.Should().ThrowExactly<InvalidDataException>().WithMessage("test is truncated");
            skip.Should().ThrowExactly<InvalidDataException>().WithMessage("test is truncated");
        }
    }
}
