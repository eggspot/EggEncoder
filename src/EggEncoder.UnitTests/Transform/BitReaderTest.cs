using EggEncoder.Transform;
using FluentAssertions;

namespace EggEncoder.UnitTests.Transform
{
    public class BitReaderTest
    {
        [Fact]
        public void ReadBits_SingleByte_Should_ReadMsbFirst()
        {
            var reader = new BitReader([0b10110010]);

            reader.ReadBits(1).Should().Be(1u);
            reader.ReadBits(1).Should().Be(0u);
            reader.ReadBits(4).Should().Be(0b1100u);
            reader.ReadBits(2).Should().Be(0b10u);
        }

        [Fact]
        public void ReadBits_AcrossByteBoundary_Should_ReadCorrectValue()
        {
            var reader = new BitReader([0b11110000, 0b00001111]);

            reader.ReadBits(4).Should().Be(0b1111u);
            reader.ReadBits(8).Should().Be(0b00000000u);
            reader.ReadBits(4).Should().Be(0b1111u);
        }

        [Fact]
        public void ReadBits_ZeroBits_Should_ReturnZero_And_NotAdvancePosition()
        {
            var reader = new BitReader([0xFF]);

            reader.ReadBits(0).Should().Be(0u);
            reader.BitPosition.Should().Be(0);
        }

        [Fact]
        public void PeekBits_Should_Not_AdvancePosition()
        {
            var reader = new BitReader([0b10110010]);

            var peeked = reader.PeekBits(4);
            var read = reader.ReadBits(4);

            read.Should().Be(peeked);
            reader.BitPosition.Should().Be(4);
        }

        [Fact]
        public void SkipBits_Should_AdvancePosition_WithoutReturningValue()
        {
            var reader = new BitReader([0b10110010, 0b01000000]);

            reader.SkipBits(8);

            reader.BitPosition.Should().Be(8);
            reader.ReadBits(2).Should().Be(0b01u);
        }

        [Fact]
        public void ByteAlign_Should_AdvanceToNextByteBoundary()
        {
            var reader = new BitReader([0b10110010, 0b01000000]);

            reader.ReadBits(3);
            reader.ByteAlign();

            reader.BitPosition.Should().Be(8);
            reader.ReadBits(2).Should().Be(0b01u);
        }

        [Fact]
        public void ByteAlign_WhenAlreadyAligned_Should_NotAdvance()
        {
            var reader = new BitReader([0b10110010, 0b01000000]);

            reader.ReadBits(8);
            reader.ByteAlign();

            reader.BitPosition.Should().Be(8);
        }

        [Fact]
        public void RemainingBits_Should_ReflectBufferLength_MinusPosition()
        {
            var reader = new BitReader([0xFF, 0xFF]);

            reader.RemainingBits.Should().Be(16);

            reader.ReadBits(5);

            reader.RemainingBits.Should().Be(11);
        }

        [Fact]
        public void ReadSignedBits_WithHighBitSet_Should_SignExtendToNegative()
        {
            var reader = new BitReader([0b10000000]);

            reader.ReadSignedBits(4).Should().Be(-8);
        }

        [Fact]
        public void ReadSignedBits_WithHighBitClear_Should_ReturnPositiveValue()
        {
            var reader = new BitReader([0b01110000]);

            reader.ReadSignedBits(4).Should().Be(7);
        }

        [Fact]
        public void ReadSignedBits_FullWidth32_Should_ReturnTheRawTwosComplementValue()
        {
            var reader = new BitReader([0xFF, 0xFF, 0xFF, 0xFF]);

            reader.ReadSignedBits(32).Should().Be(-1);
        }

        [Fact]
        public void ReadSignedBits_SingleBit_Should_ReadMinusOneOrZero()
        {
            var reader = new BitReader([0b10000000]);

            reader.ReadSignedBits(1).Should().Be(-1);
            reader.ReadSignedBits(1).Should().Be(0);
        }

        [Fact]
        public void ReadUnary_Should_CountLeadingZeros_And_ConsumeTheTerminatingOne()
        {
            var reader = new BitReader([0b00010000]);

            reader.ReadUnary().Should().Be(3u);
            reader.BitPosition.Should().Be(4);
        }

        [Fact]
        public void ReadUnary_WithNoLeadingZeros_Should_ReturnZero()
        {
            var reader = new BitReader([0b10000000]);

            reader.ReadUnary().Should().Be(0u);
            reader.BitPosition.Should().Be(1);
        }

        [Fact]
        public void ReadUnary_AcrossByteBoundary_Should_CountCorrectly()
        {
            var reader = new BitReader([0b00000000, 0b00000001]);

            reader.ReadUnary().Should().Be(15u);
            reader.BitPosition.Should().Be(16);
        }

        [Fact]
        public void ReadUnary_WithNoTerminatingOne_Should_Throw()
        {
            var reader = new BitReader([0b00000000]);

            var act = () => reader.ReadUnary();
            act.Should().ThrowExactly<EndOfStreamException>();
        }

        [Fact]
        public void ReadBits_PastEndOfBuffer_Should_Throw()
        {
            var reader = new BitReader([0xFF]);

            reader.ReadBits(4);

            var act = () => reader.ReadBits(5);
            act.Should().ThrowExactly<EndOfStreamException>();
        }

        [Fact]
        public void ReadBits_MoreThan32_Should_Throw()
        {
            var reader = new BitReader([0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);

            var act = () => reader.ReadBits(33);
            act.Should().ThrowExactly<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Constructor_WithByteOffset_Should_StartReadingFromOffset()
        {
            var reader = new BitReader([0b11111111, 0b10110010], byteOffset: 1);

            reader.BitPosition.Should().Be(8);
            reader.ReadBits(4).Should().Be(0b1011u);
        }

        [Fact]
        public void SkipToBitPosition_Should_MoveToExactPosition()
        {
            var reader = new BitReader([0b10110010, 0b01000000]);

            reader.SkipToBitPosition(9);

            reader.BitPosition.Should().Be(9);
            reader.ReadBits(1).Should().Be(0b1u);
        }

        [Fact]
        public void SkipToBitPosition_Backwards_Should_Throw()
        {
            var reader = new BitReader([0xFF]);
            reader.ReadBits(4);

            var act = () => reader.SkipToBitPosition(2);
            act.Should().ThrowExactly<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void SkipToBitPosition_PastEnd_Should_Throw()
        {
            var reader = new BitReader([0xFF]);

            var act = () => reader.SkipToBitPosition(9);
            act.Should().ThrowExactly<EndOfStreamException>();
        }

        [Fact]
        public void Constructor_WithByteLength_Should_LimitRemainingBits()
        {
            var reader = new BitReader([0xFF, 0xFF, 0xFF], byteOffset: 0, byteLength: 2);

            reader.RemainingBits.Should().Be(16);
        }
    }
}
