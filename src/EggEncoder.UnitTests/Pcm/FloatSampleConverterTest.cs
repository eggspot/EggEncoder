using EggEncoder.Pcm;
using FluentAssertions;

namespace EggEncoder.UnitTests.Pcm
{
    public class FloatSampleConverterTest
    {
        [Fact]
        public void FromFloat_FullScaleValues_Should_Map_To_The_32Bit_Native_Range()
        {
            var result = FloatSampleConverter.FromFloat([1.0f, -1.0f, 0.0f]);

            // -1.0 maps to -int.MaxValue, not int.MinValue -- same asymmetric convention WavReader
            // already uses (Float32ToInt32), which this type mirrors exactly.
            result.Should().Equal(int.MaxValue, -int.MaxValue, 0);
        }

        [Fact]
        public void FromFloat_OutOfRangeValues_Should_Clamp()
        {
            var result = FloatSampleConverter.FromFloat([2.0f, -2.0f]);

            result.Should().Equal(int.MaxValue, -int.MaxValue);
        }

        [Fact]
        public void ToFloat_32Bit_FullScaleValues_Should_Map_To_PlusMinusOne()
        {
            var result = FloatSampleConverter.ToFloat([int.MaxValue, -int.MaxValue, 0], bitsPerSample: 32);

            result[0].Should().BeApproximately(1.0f, 0.0001f);
            result[1].Should().BeApproximately(-1.0f, 0.0001f);
            result[2].Should().Be(0.0f);
        }

        [Fact]
        public void ToFloat_16Bit_Should_Normalize_Against_The_16Bit_Native_Range_Not_Int32()
        {
            var result = FloatSampleConverter.ToFloat([32767, -32768], bitsPerSample: 16);

            result[0].Should().BeApproximately(1.0f, 0.0001f);
            result[1].Should().BeApproximately(-1.0f, 0.0001f);
        }

        [Fact]
        public void RoundTrip_FromFloat_Then_ToFloat_Should_Approximately_Recover_The_Original()
        {
            float[] original = [0.5f, -0.25f, 0.75f, -1.0f, 1.0f, 0.0f];

            var intSamples = FloatSampleConverter.FromFloat(original);
            var roundTripped = FloatSampleConverter.ToFloat(intSamples, bitsPerSample: 32);

            for (var i = 0; i < original.Length; i++)
            {
                roundTripped[i].Should().BeApproximately(original[i], 0.0001f);
            }
        }

        [Fact]
        public void ToFloat_InvalidBitDepth_Should_Throw()
        {
            var act = () => FloatSampleConverter.ToFloat([1, 2], bitsPerSample: 12);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void FromFloat_EmptySpan_Should_Return_Empty_Array()
        {
            FloatSampleConverter.FromFloat([]).Should().BeEmpty();
        }

        [Fact]
        public void FromFloat_NaN_Should_Map_To_Silence_Not_IntMinValue()
        {
            // A plain (int)double.NaN cast is unspecified by the C# spec and, in practice on .NET,
            // evaluates to int.MinValue -- full-scale noise, not silence. NaN isn't a valid sample under
            // any convention, but a synthesized or third-party float source could still produce one, so
            // this is defined explicitly rather than left to fall through to that cast.
            var result = FloatSampleConverter.FromFloat([float.NaN]);

            result.Should().Equal(0);
        }

        [Theory]
        [InlineData(float.PositiveInfinity, int.MaxValue)]
        [InlineData(float.NegativeInfinity, -int.MaxValue)]
        public void FromFloat_Infinity_Should_Clamp_To_FullScale(float input, int expected)
        {
            var result = FloatSampleConverter.FromFloat([input]);

            result.Should().Equal(expected);
        }
    }
}
