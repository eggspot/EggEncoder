using EggEncoder.Transform;
using FluentAssertions;

namespace EggEncoder.UnitTests.Transform
{
    public class MdctTest
    {
        [Fact]
        public void Forward_Should_MatchDirectDefinition_ForKnownInput()
        {
            var samples = new double[] { 1, 0, 0, 0 };

            var coefficients = Mdct.Forward(samples);

            coefficients.Should().HaveCount(2);
            coefficients[0].Should().BeApproximately(Math.Cos((Math.PI / 2) * 1.5 * 0.5), 1e-12);
            coefficients[1].Should().BeApproximately(Math.Cos((Math.PI / 2) * 1.5 * 1.5), 1e-12);
        }

        [Fact]
        public void Forward_WithOddLengthInput_Should_Throw()
        {
            var act = () => Mdct.Forward([1, 2, 3]);
            act.Should().ThrowExactly<ArgumentException>();
        }

        [Fact]
        public void Forward_Inverse_WithSineWindowOverlapAdd_Should_ReconstructOriginalSignal_UpToConstantScale()
        {
            const int coefficientCount = 64;
            const int blockSize = coefficientCount * 2;
            const int blockCount = 8;
            const int signalLength = (blockCount - 1) * coefficientCount;
            const int paddedLength = signalLength + (2 * coefficientCount);

            var window = new double[blockSize];
            for (var n = 0; n < blockSize; n++)
            {
                window[n] = Math.Sin((Math.PI / blockSize) * (n + 0.5));
            }

            var random = new Random(42);
            var paddedSignal = new double[paddedLength];
            for (var i = coefficientCount; i < coefficientCount + signalLength; i++)
            {
                paddedSignal[i] = (random.NextDouble() * 2) - 1;
            }

            var reconstructed = new double[paddedLength];

            for (var block = 0; block < blockCount; block++)
            {
                var start = block * coefficientCount;
                var windowedInput = new double[blockSize];
                for (var n = 0; n < blockSize; n++)
                {
                    windowedInput[n] = paddedSignal[start + n] * window[n];
                }

                var coefficients = Mdct.Forward(windowedInput);
                var timeDomain = Mdct.Inverse(coefficients);

                for (var n = 0; n < blockSize; n++)
                {
                    reconstructed[start + n] += timeDomain[n] * window[n];
                }
            }

            var overlapStart = coefficientCount;
            var overlapEnd = coefficientCount + signalLength;

            var dotProduct = 0.0;
            var normSquared = 0.0;
            for (var i = overlapStart; i < overlapEnd; i++)
            {
                dotProduct += reconstructed[i] * paddedSignal[i];
                normSquared += paddedSignal[i] * paddedSignal[i];
            }

            var scale = dotProduct / normSquared;
            Math.Abs(scale).Should().BeGreaterThan(0);

            for (var i = overlapStart; i < overlapEnd; i++)
            {
                reconstructed[i].Should().BeApproximately(paddedSignal[i] * scale, Math.Abs(scale) * 1e-6, $"sample {i} should match");
            }
        }
    }
}
