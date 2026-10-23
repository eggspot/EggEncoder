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

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(8)]
        [InlineData(16)]
        [InlineData(18)]
        [InlineData(64)]
        [InlineData(128)]
        [InlineData(256)]
        [InlineData(1024)]
        [InlineData(2048)]
        public void Forward_AnySize_Should_MatchTheDirectDefinition(int coefficientCount)
        {
            var samples = RandomSignal(seed: coefficientCount, length: 2 * coefficientCount, amplitude: 32768);

            var coefficients = Mdct.Forward(samples);

            AssertClose(coefficients, ReferenceForward(samples));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(8)]
        [InlineData(16)]
        [InlineData(18)]
        [InlineData(64)]
        [InlineData(128)]
        [InlineData(256)]
        [InlineData(1024)]
        [InlineData(2048)]
        public void Inverse_AnySize_Should_MatchTheDirectDefinition(int coefficientCount)
        {
            var coefficients = RandomSignal(seed: coefficientCount + 1, length: coefficientCount, amplitude: 1e6);

            var samples = Mdct.Inverse(coefficients);

            AssertClose(samples, ReferenceInverse(coefficients));
        }

        [Theory]
        [InlineData(18)]
        [InlineData(200)]
        public void Forward_Inverse_NonPowerOfTwoSize_Should_BeBitIdenticalToTheDirectDefinition(int coefficientCount)
        {
            // MP3's 18-coefficient MDCT keeps the direct sums (cached cosines for small sizes, plain
            // Math.Cos above the cache limit, as for 200 here), so its output stays bit for bit.
            var samples = RandomSignal(seed: 7, length: 2 * coefficientCount, amplitude: 1);
            var coefficients = RandomSignal(seed: 8, length: coefficientCount, amplitude: 1);

            Mdct.Forward(samples).Should().Equal(ReferenceForward(samples));
            Mdct.Inverse(coefficients).Should().Equal(ReferenceInverse(coefficients));
        }

        [Fact]
        public void Forward_Inverse_EmptyInput_Should_ReturnEmptyOutput()
        {
            Mdct.Forward([]).Should().BeEmpty();
            Mdct.Inverse([]).Should().BeEmpty();
        }

        [Fact]
        public void Forward_Inverse_CalledFromManyThreadsAtOnce_Should_MatchSingleThreadedResults()
        {
            // Plans and cosine tables are shared across threads; scratch buffers are per call.
            var sizes = new[] { 18, 128, 1024 };
            var inputs = sizes.Select(size => RandomSignal(seed: size, length: 2 * size, amplitude: 1000)).ToArray();
            var expected = inputs.Select(input => Mdct.Inverse(Mdct.Forward(input))).ToArray();
            var mismatches = 0;

            Parallel.For(0, 300, iteration =>
            {
                var which = iteration % sizes.Length;
                if (!Mdct.Inverse(Mdct.Forward(inputs[which])).SequenceEqual(expected[which]))
                {
                    Interlocked.Increment(ref mismatches);
                }
            });

            mismatches.Should().Be(0);
        }

        private static double[] RandomSignal(int seed, int length, double amplitude)
        {
            var random = new Random(seed);
            var signal = new double[length];
            for (var i = 0; i < length; i++)
            {
                signal[i] = ((random.NextDouble() * 2) - 1) * amplitude;
            }

            return signal;
        }

        // The textbook definitions, with the same cosine expression the direct path uses.
        private static double[] ReferenceForward(double[] samples)
        {
            var count = samples.Length / 2;
            var coefficients = new double[count];
            for (var k = 0; k < count; k++)
            {
                var sum = 0.0;
                for (var n = 0; n < samples.Length; n++)
                {
                    sum += samples[n] * Math.Cos((Math.PI / count) * (n + 0.5 + (count / 2.0)) * (k + 0.5));
                }

                coefficients[k] = sum;
            }

            return coefficients;
        }

        private static double[] ReferenceInverse(double[] coefficients)
        {
            var count = coefficients.Length;
            var samples = new double[count * 2];
            var normalization = 1.0 / count;
            for (var n = 0; n < samples.Length; n++)
            {
                var sum = 0.0;
                for (var k = 0; k < count; k++)
                {
                    sum += coefficients[k] * Math.Cos((Math.PI / count) * (n + 0.5 + (count / 2.0)) * (k + 0.5));
                }

                samples[n] = sum * normalization;
            }

            return samples;
        }

        // Within 1e-10 of the largest magnitude: the fast path's rounding is about 1e-12 relative.
        private static void AssertClose(double[] actual, double[] expected)
        {
            actual.Should().HaveCount(expected.Length);
            var scale = expected.Length == 0 ? 1 : expected.Max(Math.Abs);
            for (var i = 0; i < expected.Length; i++)
            {
                actual[i].Should().BeApproximately(expected[i], scale * 1e-10, $"index {i}");
            }
        }
    }
}
