using EggEncoder.Codecs.Mp3;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Mp3
{
    public class Mp3AliasReductionTest
    {
        private const int CoefficientCount = 32 * 18;

        [Fact]
        public void Decode_Should_BeTheExactInverseOfEncode()
        {
            var original = BuildRampCoefficients();
            var xr = (double[])original.Clone();

            Mp3AliasReduction.Encode(xr);
            Mp3AliasReduction.Decode(xr);

            for (var i = 0; i < CoefficientCount; i++)
            {
                xr[i].Should().BeApproximately(original[i], 1e-9);
            }
        }

        [Fact]
        public void Encode_Should_BeTheExactInverseOfDecode()
        {
            var original = BuildRampCoefficients();
            var xr = (double[])original.Clone();

            Mp3AliasReduction.Decode(xr);
            Mp3AliasReduction.Encode(xr);

            for (var i = 0; i < CoefficientCount; i++)
            {
                xr[i].Should().BeApproximately(original[i], 1e-9);
            }
        }

        [Fact]
        public void Decode_WithWrongLength_Should_Throw()
        {
            var act = () => Mp3AliasReduction.Decode(new double[10]);

            act.Should().ThrowExactly<ArgumentException>();
        }

        private static double[] BuildRampCoefficients()
        {
            var xr = new double[CoefficientCount];
            for (var i = 0; i < CoefficientCount; i++)
            {
                xr[i] = (i - (CoefficientCount / 2.0)) * 0.37;
            }

            return xr;
        }
    }
}
