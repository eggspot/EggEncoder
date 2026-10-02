using EggEncoder.Codecs.Alac;
using EggEncoder.Transform;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Alac
{
    public class AlacRiceCoderTest
    {
        [Fact]
        public void EncodeResiduals_Then_DecodeResiduals_Should_RoundTrip_SmallValues()
        {
            // history starts at mb=10, giving k=1 on the very first sample -- exercises the k==1
            // "no remainder bits" branch from the first value.
            var residuals = new[] { 0, 1, -1, 2, -2, 3, -3, 0, 0, 5 };

            AssertRoundTrips(residuals, bitsPerSample: 16, pb: 40, mb: 10, kb: 14, riceHistoryMultiplier: 4);
        }

        [Fact]
        public void EncodeResiduals_Then_DecodeResiduals_Should_RoundTrip_LargeValue_Via_EscapeCode()
        {
            // zigzag(5) = 10, and k=1 at the start means divisor=1, so quotient=10 > 8 -- forces the
            // 9-ones "escape" unary code on the very first residual.
            var residuals = new[] { 5, -5, 5 };

            AssertRoundTrips(residuals, bitsPerSample: 16, pb: 40, mb: 10, kb: 14, riceHistoryMultiplier: 4);
        }

        [Fact]
        public void EncodeResiduals_Then_DecodeResiduals_Should_RoundTrip_ZeroRun_Escape_With_NonZero_Run()
        {
            // history(10) < 128 right after the first sample, forcing a run-length escape read/write;
            // residuals[1..2] really are zero, so this exercises blockSize > 0 (an actual run).
            var residuals = new[] { 0, 0, 0, 7, -3, 0, 0, 0, 0, 2 };

            AssertRoundTrips(residuals, bitsPerSample: 16, pb: 40, mb: 10, kb: 14, riceHistoryMultiplier: 4);
        }

        [Fact]
        public void EncodeResiduals_Then_DecodeResiduals_Should_RoundTrip_ZeroRun_Escape_With_NoRun()
        {
            // Same escape trigger (history < 128 after sample 0), but residuals[1] is already nonzero,
            // so the escape must still read/write a block size of exactly 0 (blockSize == 0 branch).
            var residuals = new[] { 0, 9, -4, 2 };

            AssertRoundTrips(residuals, bitsPerSample: 16, pb: 40, mb: 10, kb: 14, riceHistoryMultiplier: 4);
        }

        [Fact]
        public void EncodeResiduals_Then_DecodeResiduals_Should_RoundTrip_SignModifier_History_Overflow()
        {
            // A zero-run escape (history < 128) immediately followed by a residual whose zigzag value,
            // plus the +1 sign-modifier carried from that escape, exceeds 0xFFFF -- the one place
            // UpdateHistory's "x > 0xffff" clamp (rather than its normal update formula) fires.
            // Residual 32768 is outside what AlacFrameEncoder's own safety check ever allows through
            // (see AlacFrameEncoder's verbatim fallback), but AlacRiceCoder's own contract -- correctly
            // round-tripping whatever residual stream it's given -- is tested independently of that.
            var residuals = new[] { 0, 0, 32768 };

            AssertRoundTrips(residuals, bitsPerSample: 16, pb: 40, mb: 10, kb: 14, riceHistoryMultiplier: 4);
        }

        [Fact]
        public void EncodeResiduals_Then_DecodeResiduals_Should_RoundTrip_EmptyResidualStream()
        {
            AssertRoundTrips([], bitsPerSample: 16, pb: 40, mb: 10, kb: 14, riceHistoryMultiplier: 4);
        }

        [Fact]
        public void EncodeResiduals_Then_DecodeResiduals_Should_RoundTrip_LargeRandomStream()
        {
            var random = new Random(1234);
            var residuals = new int[5000];
            for (var i = 0; i < residuals.Length; i++)
            {
                residuals[i] = random.Next(-2000, 2000);
            }

            AssertRoundTrips(residuals, bitsPerSample: 16, pb: 40, mb: 10, kb: 14, riceHistoryMultiplier: 4);
        }

        private static void AssertRoundTrips(int[] residuals, int bitsPerSample, int pb, int mb, int kb, int riceHistoryMultiplier)
        {
            var writer = new BitWriter();
            AlacRiceCoder.EncodeResiduals(writer, residuals, residuals.Length, bitsPerSample, pb, mb, kb, riceHistoryMultiplier);

            var reader = new BitReader(writer.ToArray());
            var decoded = AlacRiceCoder.DecodeResiduals(reader, residuals.Length, bitsPerSample, pb, mb, kb, riceHistoryMultiplier);

            decoded.Should().Equal(residuals);
        }
    }
}
