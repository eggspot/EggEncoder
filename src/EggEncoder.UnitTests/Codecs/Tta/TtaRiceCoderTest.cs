using EggEncoder.Codecs.Tta;
using EggEncoder.Transform;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Tta
{
    public class TtaRiceCoderTest
    {
        [Fact]
        public void EncodeResidual_Then_DecodeResidual_Should_RoundTrip_SmallValues()
        {
            // zigzag(1)=1, zigzag(-1)=2, etc. -- all well under Shift1[k0=10]=1024, so every value
            // here takes the depth=0 ("small") path, never touching k1/sum1 at all.
            var residuals = new[] { 0, 1, -1, 2, -2, 5, -5, 0, 3, -3 };

            AssertRoundTrips(residuals);
        }

        [Fact]
        public void EncodeResidual_Then_DecodeResidual_Should_RoundTrip_LargeValue_TakingTheDepth1Path()
        {
            // zigzag(1000)=1999 &gt;= Shift1[k0=10]=1024 -- forces the depth=1 ("large") path on both
            // sides: decode's case-1 block (adapting k1/sum1, then falling through to adapt k0/sum0
            // too), and encode's "outval &gt;= Shift1[k]" branch (adapting k1/sum1, then writing a
            // non-trivial unary prefix).
            var residuals = new[] { 1000, -1000, 1000 };

            AssertRoundTrips(residuals);
        }

        [Fact]
        public void EncodeResidual_Repeated_Zero_Should_Decrement_K0()
        {
            // Hand-computed: k0 starts at 10, sum0 starts at Shift16(10)=16384. Encoding residual=0
            // (outval=0) updates sum0 = 16384 + 0 - (16384&gt;&gt;4) = 16384-1024 = 15360, which is &lt;
            // Shift16(10)=16384 -- so k0 decrements to 9 after exactly one zero. This is reachable
            // through the public API only via its *effect* (a subsequent value's k changes), so this
            // just confirms the round trip holds across that transition, not the internal k value
            // directly -- TtaRiceCoder has no exposed way to assert k0 without duplicating its logic.
            var residuals = new[] { 0, 0, 0, 1, -1, 2 };

            AssertRoundTrips(residuals);
        }

        [Fact]
        public void EncodeResidual_FirstValue_Large_Should_Increment_K0()
        {
            // Hand-computed: zigzag(10000)=19999. sum0 update: 16384+19999-1024=35359, which is &gt;
            // Shift16(11)=32768 -- so k0 increments to 11 after this one call (before any decrement
            // has had a chance to happen), the mirror of the decrement case above.
            var residuals = new[] { 10000, -3, 7, -1 };

            AssertRoundTrips(residuals);
        }

        [Fact]
        public void EncodeResidual_ExtremeValue_Should_RoundTrip_Through_A_MultiChunk_UnaryWrite()
        {
            // zigzag(10_000_000)-Shift1[k0=10]=19998975, unary=1+(that&gt;&gt;k1=10)=19531 -- far more
            // than the 31-bit chunks EncodeResidual's inner while loop writes at a time (a plain
            // single put_bits call can't write a 19531-bit run at once; this is why the loop exists
            // at all), so this exercises that loop actually iterating many times, not just once.
            var residuals = new[] { 10_000_000, -1, 2 };

            AssertRoundTrips(residuals);
        }

        [Fact]
        public void EncodeResidual_Then_DecodeResidual_Should_RoundTrip_LongZeroRun()
        {
            // 300 zeros in a row drives k0 down through several levels (each level's sum0 decays
            // geometrically toward the next threshold), plausibly reaching k0=0 -- the one case where
            // DecodeResidual's "k != 0" ternary takes its false branch (value=unary directly, no
            // remainder bits) and EncodeResidual's "if (k != 0)" guard skips writing remainder bits
            // entirely. Not hand-proven to hit exactly k0=0 (that depends on exact integer-truncation
            // behavior at every one of ~100 decay steps), but the round trip holds regardless of
            // exactly which k values occur along the way.
            var residuals = new int[300];

            AssertRoundTrips(residuals);
        }

        [Fact]
        public void EncodeResidual_Then_DecodeResidual_Should_RoundTrip_LargeRandomStream()
        {
            var random = new Random(99);
            var residuals = new int[5000];
            for (var i = 0; i < residuals.Length; i++)
            {
                residuals[i] = random.Next(-5000, 5000);
            }

            AssertRoundTrips(residuals);
        }

        [Fact]
        public void EncodeResidual_Then_DecodeResidual_Should_RoundTrip_EmptyStream()
        {
            AssertRoundTrips([]);
        }

        private static void AssertRoundTrips(int[] residuals)
        {
            var writer = new BitWriter();
            var encodeCoder = new TtaRiceCoder();
            foreach (var residual in residuals)
            {
                encodeCoder.EncodeResidual(writer, residual);
            }

            var reader = new BitReader(writer.ToArray());
            var decodeCoder = new TtaRiceCoder();
            var decoded = new int[residuals.Length];
            for (var i = 0; i < residuals.Length; i++)
            {
                decoded[i] = decodeCoder.DecodeResidual(reader);
            }

            decoded.Should().Equal(residuals);
        }
    }
}
