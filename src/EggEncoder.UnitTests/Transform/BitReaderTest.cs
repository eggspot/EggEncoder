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

        [Fact]
        public void ReadBits_AllWidths_AtEveryBitOffset_Should_MatchABitByBitReference()
        {
            // 20 bytes covers both LoadWindow paths: a single 8-byte load while 8 bytes remain, then
            // the byte-at-a-time tail once fewer do.
            var data = RandomBytes(seed: 1, length: 20);

            for (var start = 0; start < 8; start++)
            {
                for (var width = 0; width <= 32 && start + width <= data.Length * 8; width++)
                {
                    for (var position = start; position + width <= data.Length * 8; position += 13)
                    {
                        var reader = new BitReader(data);
                        reader.SkipBits(position);

                        reader.ReadBits(width).Should().Be(ReferenceReadBits(data, position, width), $"{width} bits at bit {position}");
                        reader.BitPosition.Should().Be(position + width);
                    }
                }
            }
        }

        [Fact]
        public void ReadBits_RandomOperationSequence_Should_MatchABitByBitReference()
        {
            var data = RandomBytes(seed: 2, length: 4096);
            var random = new Random(3);
            var reader = new BitReader(data);
            var position = 0;

            while (true)
            {
                if (random.Next(4) == 0)
                {
                    var zeros = ReferenceUnaryLength(data, position, data.Length * 8);
                    if (zeros < 0)
                    {
                        break;
                    }

                    reader.ReadUnary().Should().Be((uint)zeros, $"unary at bit {position}");
                    position += zeros + 1;
                }
                else
                {
                    var width = random.Next(33);
                    if (position + width > data.Length * 8)
                    {
                        break;
                    }

                    reader.ReadBits(width).Should().Be(ReferenceReadBits(data, position, width), $"{width} bits at bit {position}");
                    position += width;
                }

                reader.BitPosition.Should().Be(position);
            }
        }

        [Fact]
        public void ReadUnary_RunLongerThanOneWindow_Should_CountEveryZero()
        {
            // 150 zero bits then a one: the scan must carry its count across several 64-bit windows,
            // starting from an unaligned bit position.
            var data = new byte[24];
            data[18] = 0b0100_0000; // bit 145
            var reader = new BitReader(data);
            reader.ReadBits(3);

            reader.ReadUnary().Should().Be(142u);
            reader.BitPosition.Should().Be(146);
        }

        [Fact]
        public void ReadUnary_WithOnlyZerosBeforeByteLength_Should_Throw_EvenIfTheBufferContinues()
        {
            // The one bit lies past byteLength: a reader limited to the first 9 bytes must not see it.
            var data = new byte[16];
            data[9] = 0xFF;
            var reader = new BitReader(data, byteOffset: 0, byteLength: 9);

            var act = () => reader.ReadUnary();

            act.Should().ThrowExactly<EndOfStreamException>();
            reader.BitPosition.Should().Be(72);
        }

        [Fact]
        public void ReadUnary_TerminatingOneOnTheLastBitBeforeByteLength_Should_BeFound()
        {
            var data = new byte[16];
            data[8] = 0b0000_0001;
            data[9] = 0xFF;
            var reader = new BitReader(data, byteOffset: 0, byteLength: 9);

            reader.ReadUnary().Should().Be(71u);
            reader.RemainingBits.Should().Be(0);
        }

        [Fact]
        public void ReadBits_WithByteLengthPastTheBuffer_Should_ThrowIndexOutOfRange_LikeBefore()
        {
            // A reader constructed past its buffer's end has always failed on the first missing
            // byte rather than reading zeros; the word-at-a-time load keeps that.
            var reader = new BitReader([0xAB], byteOffset: 0, byteLength: 2);

            reader.ReadBits(8).Should().Be(0xABu);
            var act = () => reader.ReadBits(4);

            act.Should().ThrowExactly<IndexOutOfRangeException>();
        }

        [Fact]
        public void ReadUnary_WithByteLengthPastTheBuffer_Should_ThrowIndexOutOfRange_LikeBefore()
        {
            var reader = new BitReader([0x00], byteOffset: 0, byteLength: 2);

            var act = () => reader.ReadUnary();

            act.Should().ThrowExactly<IndexOutOfRangeException>();
        }

        [Theory]
        [InlineData(new byte[] { 0x80 }, 2, 0u, 1)]
        [InlineData(new byte[] { 0x00, 0x01 }, 5, 15u, 16)]
        [InlineData(new byte[] { 0x33, 0xB5, 0x00, 0xC4, 0xC7 }, 14, 2u, 3)]
        [InlineData(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x08 }, 12, 76u, 77)]
        public void ReadUnary_WithByteLengthPastTheBuffer_And_AOneBitInTheBuffer_Should_FindIt(byte[] data, int byteLength, uint expectedZeros, int expectedPosition)
        {
            var reader = new BitReader(data, byteOffset: 0, byteLength: byteLength);

            var zeros = reader.ReadUnary();

            zeros.Should().Be(expectedZeros);
            reader.BitPosition.Should().Be(expectedPosition);
        }

        [Fact]
        public void ReadBits_FromANegativeBitPosition_Should_ThrowIndexOutOfRange()
        {
            var reader = new BitReader([0xFF, 0xFF]);
            reader.SkipBits(-3);

            var act = () => reader.ReadBits(4);

            act.Should().ThrowExactly<IndexOutOfRangeException>();
        }

        [Fact]
        public void ReadBits_FromAByteOffsetReader_Should_NotSeeTheBytesBeforeIt()
        {
            var data = RandomBytes(seed: 4, length: 32);
            var reader = new BitReader(data, byteOffset: 5, byteLength: 20);

            reader.ReadBits(32).Should().Be(ReferenceReadBits(data, 40, 32));
            reader.ReadBits(17).Should().Be(ReferenceReadBits(data, 72, 17));
        }

        private static byte[] RandomBytes(int seed, int length)
        {
            var data = new byte[length];
            new Random(seed).NextBytes(data);

            // Clear some runs so ReadUnary sees long zero stretches, not just ~1-bit ones.
            for (var i = 0; i < length; i += 37)
            {
                Array.Clear(data, i, Math.Min(9, length - i));
            }

            return data;
        }

        private static uint ReferenceReadBits(byte[] data, int position, int width)
        {
            var value = 0u;
            for (var i = 0; i < width; i++)
            {
                var bit = (data[(position + i) / 8] >> (7 - ((position + i) % 8))) & 1;
                value = (value << 1) | (uint)bit;
            }

            return value;
        }

        // The number of zero bits before the next one bit, or -1 if none appears before bitLength.
        private static int ReferenceUnaryLength(byte[] data, int position, int bitLength)
        {
            for (var i = position; i < bitLength; i++)
            {
                if (ReferenceReadBits(data, i, 1) == 1)
                {
                    return i - position;
                }
            }

            return -1;
        }
    }
}
