using EggEncoder.Codecs.Aiff;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Aiff
{
    public class IeeeExtendedFloatTest
    {
        [Theory]
        [InlineData(8000.0)]
        [InlineData(11025.0)]
        [InlineData(22050.0)]
        [InlineData(44100.0)]
        [InlineData(48000.0)]
        [InlineData(88200.0)]
        [InlineData(96000.0)]
        [InlineData(192000.0)]
        [InlineData(1.0)]
        [InlineData(1.5)]
        [InlineData(0.25)]
        public void ToDouble_Of_FromDouble_Should_Round_Trip_Exactly(double value)
        {
            Span<byte> bytes = stackalloc byte[10];
            IeeeExtendedFloat.FromDouble(value, bytes);

            var result = IeeeExtendedFloat.ToDouble(bytes);

            result.Should().Be(value);
        }

        [Fact]
        public void FromDouble_Zero_Should_Write_AllZeroBytes()
        {
            Span<byte> bytes = stackalloc byte[10];
            bytes.Fill(0xFF);

            IeeeExtendedFloat.FromDouble(0.0, bytes);

            bytes.ToArray().Should().AllSatisfy(b => b.Should().Be(0));
        }

        [Fact]
        public void ToDouble_AllZeroBytes_Should_Be_Zero()
        {
            Span<byte> bytes = stackalloc byte[10];

            IeeeExtendedFloat.ToDouble(bytes).Should().Be(0.0);
        }

        [Fact]
        public void FromDouble_NegativeValue_Should_Set_SignBit_And_RoundTrip()
        {
            Span<byte> bytes = stackalloc byte[10];
            IeeeExtendedFloat.FromDouble(-44100.0, bytes);

            (bytes[0] & 0x80).Should().NotBe(0, "the sign bit should be set for a negative value");
            IeeeExtendedFloat.ToDouble(bytes).Should().Be(-44100.0);
        }

        [Fact]
        public void FromDouble_One_Should_Match_The_Known_Extended_Encoding()
        {
            // 1.0 in 80-bit extended: sign=0, biased exponent = 16383 (0x3FFF), explicit mantissa with
            // only the integer bit set (0x8000000000000000) -- hand-verified against the format spec.
            Span<byte> bytes = stackalloc byte[10];
            IeeeExtendedFloat.FromDouble(1.0, bytes);

            bytes[0].Should().Be(0x3F);
            bytes[1].Should().Be(0xFF);
            bytes[2].Should().Be(0x80);
            for (var i = 3; i < 10; i++)
            {
                bytes[i].Should().Be(0);
            }
        }

        [Fact]
        public void ToDouble_SubnormalEncoding_Should_Throw()
        {
            var bytes = new byte[10];
            bytes[9] = 1; // biasedExponent == 0, mantissa != 0

            var act = () => IeeeExtendedFloat.ToDouble(bytes);

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void ToDouble_ExponentTooSmallForDouble_Should_Throw()
        {
            // Normalized extended value (integer bit set) with the smallest possible biased exponent --
            // rebasing to double's bias (1023) drives the result exponent negative, which is out of
            // double's representable range even though it isn't the all-zero subnormal encoding.
            var bytes = new byte[10];
            bytes[0] = 0x00;
            bytes[1] = 0x01;
            bytes[2] = 0x80;

            var act = () => IeeeExtendedFloat.ToDouble(bytes);

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void ToDouble_InfinityEncoding_Should_Throw()
        {
            var bytes = new byte[10];
            bytes[0] = 0x7F;
            bytes[1] = 0xFF; // biasedExponent == 0x7FFF (all ones)
            bytes[2] = 0x80;

            var act = () => IeeeExtendedFloat.ToDouble(bytes);

            act.Should().ThrowExactly<NotSupportedException>();
        }
    }
}
