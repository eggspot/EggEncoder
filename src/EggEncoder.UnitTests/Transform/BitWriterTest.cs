using EggEncoder.Transform;
using FluentAssertions;

namespace EggEncoder.UnitTests.Transform
{
    public class BitWriterTest
    {
        [Fact]
        public void WriteBits_SingleByte_Should_WriteMsbFirst()
        {
            var writer = new BitWriter();

            writer.WriteBits(0b1, 1);
            writer.WriteBits(0b0, 1);
            writer.WriteBits(0b1100, 4);
            writer.WriteBits(0b10, 2);

            writer.ToArray().Should().Equal(new byte[] { 0b10110010 });
        }

        [Fact]
        public void WriteBits_AcrossByteBoundary_Should_ProduceCorrectBytes()
        {
            var writer = new BitWriter();

            writer.WriteBits(0b1111, 4);
            writer.WriteBits(0b00000000, 8);
            writer.WriteBits(0b1111, 4);

            writer.ToArray().Should().Equal(new byte[] { 0b11110000, 0b00001111 });
        }

        [Fact]
        public void WriteBits_ZeroBits_Should_NotAdvancePosition()
        {
            var writer = new BitWriter();

            writer.WriteBits(0xFF, 0);

            writer.BitPosition.Should().Be(0);
        }

        [Fact]
        public void BitPosition_Should_ReflectBitsWritten()
        {
            var writer = new BitWriter();

            writer.WriteBits(0b101, 3);

            writer.BitPosition.Should().Be(3);
        }

        [Fact]
        public void ByteAlign_WhenNotAligned_Should_PadWithZerosToNextByteBoundary()
        {
            var writer = new BitWriter();

            writer.WriteBits(0b101, 3);
            writer.ByteAlign();

            writer.BitPosition.Should().Be(8);
            writer.ToArray().Should().Equal(new byte[] { 0b10100000 });
        }

        [Fact]
        public void ByteAlign_WhenAlreadyAligned_Should_NotAdvance()
        {
            var writer = new BitWriter();

            writer.WriteBits(0xFF, 8);
            writer.ByteAlign();

            writer.BitPosition.Should().Be(8);
        }

        [Fact]
        public void ToArray_WithEmptyWriter_Should_ReturnEmptyArray()
        {
            var writer = new BitWriter();

            writer.ToArray().Should().BeEmpty();
        }

        [Fact]
        public void ToArray_WithPartialFinalByte_Should_PadRemainingBitsWithZeros()
        {
            var writer = new BitWriter();

            writer.WriteBits(0b1, 1);

            writer.ToArray().Should().Equal(new byte[] { 0b10000000 });
        }

        [Fact]
        public void WriteBits_ThenReadBackWithBitReader_Should_RoundTrip()
        {
            var writer = new BitWriter();

            writer.WriteBits(0x3FF, 10);
            writer.WriteBits(0x5, 3);

            var reader = new BitReader(writer.ToArray());

            reader.ReadBits(10).Should().Be(0x3FFu);
            reader.ReadBits(3).Should().Be(0x5u);
        }
    }
}
