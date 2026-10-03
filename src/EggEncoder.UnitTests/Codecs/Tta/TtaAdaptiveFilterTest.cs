using EggEncoder.Codecs.Tta;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Tta
{
    public class TtaAdaptiveFilterTest
    {
        private const int Shift = 9; // 16-bit, matching TtaFrameDecoder/Encoder's FilterShift

        [Fact]
        public void Encode_Then_Decode_Should_RoundTrip_SignAlternatingValues()
        {
            // error starts at 0 (no adaptation on the very first call -- neither the +=dx nor -=dx
            // branch), then alternates sign on every subsequent call, exercising both adaptation
            // directions (error&lt;0 and error&gt;0) throughout.
            var samples = new[] { 5, -5, 5, -5, 3, -3, 8, -8, 0, 1, -1 };

            AssertRoundTrips(samples);
        }

        [Fact]
        public void Encode_Then_Decode_Should_RoundTrip_AllZeros()
        {
            var samples = new int[20];

            AssertRoundTrips(samples);
        }

        [Fact]
        public void Encode_Then_Decode_Should_RoundTrip_MonotonicRamp()
        {
            // A steadily increasing sequence keeps error consistently positive for many calls in a
            // row, exercising sustained same-direction adaptation (not just alternating).
            var samples = new int[50];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = i * 10;
            }

            AssertRoundTrips(samples);
        }

        [Fact]
        public void Encode_Then_Decode_Should_RoundTrip_LargeRandomStream()
        {
            var random = new Random(7);
            var samples = new int[5000];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = random.Next(-32768, 32768);
            }

            AssertRoundTrips(samples);
        }

        [Fact]
        public void Encode_Should_Not_Mutate_State_Shared_With_A_Fresh_Decode_Instance()
        {
            // Two independently-constructed filters (one for encode, one for decode) must reach
            // identical internal states purely by being fed the same encode/decode sequence -- this
            // is implicit in every round-trip test above, but called out explicitly here since it's
            // the core correctness property of the whole class.
            var encodeFilter = new TtaAdaptiveFilter(Shift);
            var decodeFilter = new TtaAdaptiveFilter(Shift);

            var samples = new[] { 42, -17, 100, -100, 0, 3 };
            foreach (var sample in samples)
            {
                var residual = encodeFilter.Encode(sample);
                var reconstructed = decodeFilter.Decode(residual);
                reconstructed.Should().Be(sample);
            }
        }

        private static void AssertRoundTrips(int[] samples)
        {
            var encodeFilter = new TtaAdaptiveFilter(Shift);
            var residuals = new int[samples.Length];
            for (var i = 0; i < samples.Length; i++)
            {
                residuals[i] = encodeFilter.Encode(samples[i]);
            }

            var decodeFilter = new TtaAdaptiveFilter(Shift);
            var reconstructed = new int[samples.Length];
            for (var i = 0; i < samples.Length; i++)
            {
                reconstructed[i] = decodeFilter.Decode(residuals[i]);
            }

            reconstructed.Should().Equal(samples);
        }
    }
}
