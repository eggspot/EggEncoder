using EggEncoder.Codecs.Tta;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Tta
{
    public class TtaFixedPredictorTest
    {
        [Fact]
        public void Encode_Then_Decode_Should_RoundTrip_VariedValues()
        {
            var values = new[] { 0, 100, -100, 32767, -32768, 1, -1, 5000, -5000 };

            AssertRoundTrips(values);
        }

        [Fact]
        public void Encode_Then_Decode_Should_RoundTrip_AllZeros()
        {
            AssertRoundTrips(new int[10]);
        }

        [Fact]
        public void Encode_Then_Decode_Should_RoundTrip_LargeRandomStream()
        {
            var random = new Random(3);
            var values = new int[5000];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = random.Next(-32768, 32768);
            }

            AssertRoundTrips(values);
        }

        private static void AssertRoundTrips(int[] values)
        {
            var encoder = new TtaFixedPredictor();
            var encoded = new int[values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                encoded[i] = encoder.Encode(values[i]);
            }

            var decoder = new TtaFixedPredictor();
            var decoded = new int[values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                decoded[i] = decoder.Decode(encoded[i]);
            }

            decoded.Should().Equal(values);
        }
    }
}
