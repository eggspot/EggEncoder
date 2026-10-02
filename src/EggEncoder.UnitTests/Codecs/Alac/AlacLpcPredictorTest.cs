using EggEncoder.Codecs.Alac;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Alac
{
    public class AlacLpcPredictorTest
    {
        private static readonly int[] Seed = [160, -190, 170, -130, 80, -25];

        [Fact]
        public void Analyze_Then_Reconstruct_Should_RoundTrip_RandomSamples()
        {
            var random = new Random(7);
            var samples = new int[5000];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = random.Next(-32768, 32768);
            }

            AssertRoundTrips(samples, order: 6, quantization: 6);
        }

        [Fact]
        public void Analyze_Then_Reconstruct_Should_RoundTrip_With_ZeroOrder_PurePassthrough()
        {
            var samples = new[] { 100, -100, 32767, -32768, 0, 1, -1 };

            AssertRoundTrips(samples, order: 0, quantization: 6);
        }

        [Fact]
        public void Analyze_Then_Reconstruct_Should_RoundTrip_When_OrderExceedsSampleCount()
        {
            // Only 3 samples but a predictor order of 6 -- the main LPC loop (i = order..sampleCount-1)
            // never runs; every sample is produced by the 1st-order-delta warmup instead.
            var samples = new[] { 500, -500, 250 };

            AssertRoundTrips(samples, order: 6, quantization: 6);
        }

        [Fact]
        public void Analyze_Then_Reconstruct_Should_RoundTrip_With_ExactZeroResidual()
        {
            // A linear ramp, with a coefficient chosen so the 2nd-order predictor extrapolates it
            // exactly (predicted[i] = 2*samples[i-1] - samples[i-2]), produces an exact-zero residual
            // for every post-warmup sample -- exercises AdaptCoefficients' sign==0 early return.
            var samples = new int[20];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = i * 10;
            }

            AssertRoundTrips(samples, order: 2, quantization: 6, coefficients: [0, 128]);
        }

        [Fact]
        public void Analyze_Should_Not_Mutate_The_Original_Samples_Array()
        {
            var samples = new[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            var original = (int[])samples.Clone();

            AlacLpcPredictor.Analyze(samples, samples.Length, order: 2, quantization: 6, (int[])Seed[..2].Clone());

            samples.Should().Equal(original);
        }

        private static void AssertRoundTrips(int[] samples, int order, int quantization, int[]? coefficients = null)
        {
            var encodeCoefficients = coefficients is null ? (int[])Seed[..order].Clone() : (int[])coefficients.Clone();
            var residuals = AlacLpcPredictor.Analyze(samples, samples.Length, order, quantization, encodeCoefficients);

            var decodeCoefficients = coefficients is null ? (int[])Seed[..order].Clone() : (int[])coefficients.Clone();
            var reconstructed = AlacLpcPredictor.Reconstruct(residuals, samples.Length, order, quantization, decodeCoefficients);

            reconstructed.Should().Equal(samples);
        }
    }
}
