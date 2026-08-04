using EggEncoder.Transform;
using FluentAssertions;

namespace EggEncoder.UnitTests.Transform
{
    public class HuffmanTableTest
    {
        [Fact]
        public void Decode_Should_ReturnCorrectIndex_ForEachCodeword()
        {
            uint[] codes = [0b0, 0b10, 0b110, 0b111];
            byte[] lengths = [1, 2, 3, 3];
            var table = new HuffmanTable(codes, lengths);

            table.Decode(new BitReader([0b00000000])).Should().Be(0);
            table.Decode(new BitReader([0b10000000])).Should().Be(1);
            table.Decode(new BitReader([0b11000000])).Should().Be(2);
            table.Decode(new BitReader([0b11100000])).Should().Be(3);
        }

        [Fact]
        public void Decode_MultipleCodewordsInSequence_Should_AdvancePositionCorrectly()
        {
            uint[] codes = [0b0, 0b10, 0b110, 0b111];
            byte[] lengths = [1, 2, 3, 3];
            var table = new HuffmanTable(codes, lengths);

            var reader = new BitReader([0b01011100]);

            table.Decode(reader).Should().Be(0);
            table.Decode(reader).Should().Be(1);
            table.Decode(reader).Should().Be(3);
        }

        [Fact]
        public void Constructor_WithIncompleteCode_Should_Throw()
        {
            uint[] codes = [0b0, 0b10];
            byte[] lengths = [1, 2];

            var act = () => new HuffmanTable(codes, lengths);
            act.Should().ThrowExactly<InvalidDataException>();
        }

        [Fact]
        public void Constructor_WithMismatchedArrayLengths_Should_Throw()
        {
            uint[] codes = [0b0, 0b10];
            byte[] lengths = [1];

            var act = () => new HuffmanTable(codes, lengths);
            act.Should().ThrowExactly<ArgumentException>();
        }
    }
}
