using EggEncoder.Media;
using FluentAssertions;

namespace EggEncoder.UnitTests.Media
{
    public class MediaTimestampTest
    {
        [Fact]
        public void Rescale_SameTimescale_Should_Return_Value_Unchanged()
        {
            MediaTimestamp.Rescale(long.MaxValue, 90000, 90000).Should().Be(long.MaxValue);
        }

        [Fact]
        public void Rescale_OneAacFrameAt44100To90kHz_Should_Round_To_Nearest()
        {
            // 1024 * 90000 / 44100 = 2089.795..., the non-integer delta MpegTsMuxer avoids accumulating.
            MediaTimestamp.Rescale(1024, 44100, 90000).Should().Be(2090);
            MediaTimestamp.Rescale(-1024, 44100, 90000).Should().Be(-2090);
        }

        [Theory]
        [InlineData(1, 0)]
        [InlineData(3, 2)]
        [InlineData(5, 2)]
        [InlineData(7, 4)]
        [InlineData(-1, 0)]
        [InlineData(-3, -2)]
        [InlineData(-5, -2)]
        public void Rescale_ExactHalf_Should_Round_Half_To_Even(long value, long expected)
        {
            // value / 2 is always exactly k + 0.5 here.
            MediaTimestamp.Rescale(value, 2, 1).Should().Be(expected);
        }

        [Fact]
        public void Rescale_BelowHalf_Should_Round_Toward_Zero()
        {
            MediaTimestamp.Rescale(1, 3, 1).Should().Be(0);
            MediaTimestamp.Rescale(-1, 3, 1).Should().Be(0);
            MediaTimestamp.Rescale(2, 3, 1).Should().Be(1);
            MediaTimestamp.Rescale(-2, 3, 1).Should().Be(-1);
        }

        [Fact]
        public void Rescale_IntermediateProductPastInt64_Should_Not_Overflow()
        {
            // long.MaxValue * 90000 needs ~81 bits; the result itself fits.
            MediaTimestamp.Rescale(long.MaxValue, 90000, 1000).Should().Be(102481911520608620);
            MediaTimestamp.Rescale(long.MinValue, 90000, 1000).Should().Be(-102481911520608620);
        }

        [Fact]
        public void Rescale_ResultPastInt64_Should_Throw_OverflowException()
        {
            var tooBig = () => MediaTimestamp.Rescale(long.MaxValue, 1, 2);
            var tooSmall = () => MediaTimestamp.Rescale(long.MinValue, 1, 2);

            tooBig.Should().ThrowExactly<OverflowException>();
            tooSmall.Should().ThrowExactly<OverflowException>();
        }

        [Theory]
        [InlineData(0, 1000)]
        [InlineData(-1, 1000)]
        [InlineData(1000, 0)]
        [InlineData(1000, -1)]
        public void Rescale_NonPositiveTimescale_Should_Throw(int from, int to)
        {
            var act = () => MediaTimestamp.Rescale(1, from, to);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }
    }
}
