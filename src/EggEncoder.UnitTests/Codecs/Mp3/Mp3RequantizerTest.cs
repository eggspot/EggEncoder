using EggEncoder.Codecs.Mp3;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Mp3
{
    public class Mp3RequantizerTest
    {
        private static readonly (int IndexOfStart, int IndexOfEnd)[] Bands44100Hz = Mp3ScaleFactorTables.GetLongBlockBands(44100);
        private static readonly int[] ZeroScalefactors = new int[21];

        [Fact]
        public void Decode_WithWrongLength_Should_Throw()
        {
            var act = () => Mp3Requantizer.Decode(new int[10], ZeroScalefactors, Bands44100Hz, globalGain: 100, preflag: false, scalefacScale: false);

            act.Should().ThrowExactly<ArgumentException>();
        }

        [Fact]
        public void Decode_AllZeroInput_Should_ProduceAllZeroOutput()
        {
            var xr = Mp3Requantizer.Decode(new int[576], ZeroScalefactors, Bands44100Hz, globalGain: 150, preflag: false, scalefacScale: false);

            xr.Should().AllSatisfy(v => v.Should().Be(0));
        }

        [Fact]
        public void Decode_PreservesSign()
        {
            var quantized = new int[576];
            quantized[10] = 5;
            quantized[20] = -5;

            var xr = Mp3Requantizer.Decode(quantized, ZeroScalefactors, Bands44100Hz, globalGain: 210, preflag: false, scalefacScale: false);

            xr[10].Should().BeGreaterThan(0);
            xr[20].Should().BeGreaterThan(double.NegativeInfinity).And.BeLessThan(0);
            Math.Abs(xr[10]).Should().BeApproximately(Math.Abs(xr[20]), 1e-9);
        }

        [Fact]
        public void Decode_WithZeroScalefactorsAndNoPreflag_Should_RoundTrip_TheEncoderOwnQuantizer()
        {
            // Mp3Quantizer (the existing, already-verified encoder) always quantizes with every
            // scalefactor implicitly 0 and preflag off -- so feeding its own Plan.Quantized/
            // GlobalGain back through Mp3Requantizer.Decode with scalefactorsBySfb all zero and
            // preflag false exercises the exact same global_gain term both directions share,
            // without needing a second, independent oracle for the scalefactor/preflag terms.
            var xr = new double[576];
            for (var i = 0; i < xr.Length; i++)
            {
                xr[i] = 2000.0 * Math.Sin(2 * Math.PI * 7 * i / 576.0);
            }

            var plan = Mp3Quantizer.FindPlan(xr, maxBits: 100_000);

            var decoded = Mp3Requantizer.Decode(plan.Quantized, ZeroScalefactors, Bands44100Hz, plan.GlobalGain, preflag: false, scalefacScale: false);

            var errorSumSquares = 0.0;
            var signalSumSquares = 0.0;
            for (var i = 0; i < xr.Length; i++)
            {
                var error = xr[i] - decoded[i];
                errorSumSquares += error * error;
                signalSumSquares += xr[i] * xr[i];
            }

            var snrDb = 10 * Math.Log10(signalSumSquares / errorSumSquares);
            snrDb.Should().BeGreaterThan(30, "the inverse power-law dequantization should closely recover the original magnitudes for a signal well within the quantizer's own representable range");
        }

        [Fact]
        public void Decode_WithNonZeroScalefactor_Should_ScaleTheAffectedBandOnly()
        {
            var quantized = new int[576];
            quantized[0] = 10; // in band 0 (lines 0-3 at 44.1kHz)
            quantized[10] = 10; // in band 2 (lines 8-11 at 44.1kHz)

            var flatScalefactors = new int[21];
            var boostedScalefactors = new int[21];
            boostedScalefactors[0] = 4; // only band 0 boosted

            var flat = Mp3Requantizer.Decode(quantized, flatScalefactors, Bands44100Hz, globalGain: 210, preflag: false, scalefacScale: false);
            var boosted = Mp3Requantizer.Decode(quantized, boostedScalefactors, Bands44100Hz, globalGain: 210, preflag: false, scalefacScale: false);

            // A higher scalefactor means a SMALLER reconstructed magnitude (it subtracts from the
            // exponent) -- band 0's own value should shrink, band 2's own value (a different band)
            // should be completely unaffected.
            Math.Abs(boosted[0]).Should().BeLessThan(Math.Abs(flat[0]));
            boosted[10].Should().Be(flat[10]);
        }

        [Fact]
        public void Decode_WithPreflag_Should_FurtherAttenuateHighBands_ButNotBandsWithZeroPretab()
        {
            var quantized = new int[576];
            quantized[0] = 10; // band 0 -- pretab[0] == 0
            quantized[300] = 10; // band 19 at 44.1kHz (288-341) -- pretab[19] == 3

            var withoutPreflag = Mp3Requantizer.Decode(quantized, ZeroScalefactors, Bands44100Hz, globalGain: 210, preflag: false, scalefacScale: false);
            var withPreflag = Mp3Requantizer.Decode(quantized, ZeroScalefactors, Bands44100Hz, globalGain: 210, preflag: true, scalefacScale: false);

            withPreflag[0].Should().Be(withoutPreflag[0], "pretab[0] is 0, so preflag changes nothing for band 0");
            Math.Abs(withPreflag[300]).Should().BeLessThan(Math.Abs(withoutPreflag[300]), "pretab[19] is nonzero, so preflag must attenuate band 19's own reconstructed magnitude");
        }
    }
}
