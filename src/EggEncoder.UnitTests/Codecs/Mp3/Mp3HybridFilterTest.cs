using EggEncoder.Codecs.Mp3;
using EggEncoder.Transform;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Mp3
{
    public class Mp3HybridFilterTest
    {
        private const int CoefficientsPerSubband = 18;
        private const int BlockSize = 36;

        // Isolates the hybrid filter's own TDAC (time-domain alias cancellation) correctness from
        // everything else in the decode chain (alias reduction, polyphase synthesis, the real
        // Mp3FrameEncoder) by replicating Mp3FrameEncoder.ComputeGranuleCoefficients' own windowing/
        // Mdct.Forward/divide-by-18 steps directly, by hand, for two consecutive granules of a
        // single subband. This test caught a real bug during development: Mp3HybridFilter's own
        // IMDCT compensation initially multiplied by CoefficientsPerSubband (18) alone (undoing
        // only Transform.Mdct.Inverse's own 1/18 normalization, per Mp3FrameEncoder's own doc
        // comment) but was still off by an exact, reproducible factor of 2 -- confirmed via a
        // minimal from-scratch Python harness reproducing Transform.Mdct's exact formula: applying
        // the same sine window on BOTH the forward (encode) and inverse (here) sides of the TDAC
        // overlap-add inherently halves the reconstructed amplitude for this project's own
        // "n+0.5+K/2"-basis MDCT convention. Mp3HybridFilter now compensates with *18*2, verified
        // here to reconstruct losslessly (no quantization is involved in this path at all).
        [Fact]
        public void HybridFilterAlone_Should_ReconstructTheSecondOfTwoWindowedBlocks()
        {
            var window = new double[BlockSize];
            for (var n = 0; n < BlockSize; n++)
            {
                window[n] = Math.Sin(Math.PI / BlockSize * (n + 0.5));
            }

            var block0 = new double[CoefficientsPerSubband];
            var block1 = new double[CoefficientsPerSubband];
            for (var i = 0; i < CoefficientsPerSubband; i++)
            {
                block0[i] = Math.Sin(2 * Math.PI * 3 * i / CoefficientsPerSubband) * 100;
                block1[i] = Math.Sin(2 * Math.PI * 5 * i / CoefficientsPerSubband) * 100;
            }

            // Mirror Mp3FrameEncoder.ComputeGranuleCoefficients' own windowing + Mdct.Forward +
            // /18 exactly: granule N's own 36-sample window is [previous granule's raw samples,
            // current granule's raw samples], each half sine-windowed.
            var windowed0 = new double[BlockSize];
            for (var i = 0; i < CoefficientsPerSubband; i++)
            {
                windowed0[i] = 0 * window[i]; // granule -1 (silence) history
                windowed0[CoefficientsPerSubband + i] = block0[i] * window[CoefficientsPerSubband + i];
            }

            var coefficients0 = Mdct.Forward(windowed0);
            for (var k = 0; k < CoefficientsPerSubband; k++)
            {
                coefficients0[k] /= CoefficientsPerSubband;
            }

            var windowed1 = new double[BlockSize];
            for (var i = 0; i < CoefficientsPerSubband; i++)
            {
                windowed1[i] = block0[i] * window[i];
                windowed1[CoefficientsPerSubband + i] = block1[i] * window[CoefficientsPerSubband + i];
            }

            var coefficients1 = Mdct.Forward(windowed1);
            for (var k = 0; k < CoefficientsPerSubband; k++)
            {
                coefficients1[k] /= CoefficientsPerSubband;
            }

            // Now decode both granules through a single subband's worth of hybrid filter (xr laid
            // out as if this were subband 0 of a 32-subband granule, with every other subband 0).
            var hybrid = new Mp3HybridFilter();

            var xr0 = new double[32 * CoefficientsPerSubband];
            Array.Copy(coefficients0, xr0, CoefficientsPerSubband);
            hybrid.Process(xr0); // granule 0's own output is a cold-start artifact, discarded

            var xr1 = new double[32 * CoefficientsPerSubband];
            Array.Copy(coefficients1, xr1, CoefficientsPerSubband);
            var output1 = hybrid.Process(xr1);

            var decodedBlock0 = output1[..CoefficientsPerSubband];

            var errorSumSquares = 0.0;
            var signalSumSquares = 0.0;
            for (var i = 0; i < CoefficientsPerSubband; i++)
            {
                var error = block0[i] - decodedBlock0[i];
                errorSumSquares += error * error;
                signalSumSquares += block0[i] * block0[i];
            }

            var snrDb = 10 * Math.Log10(signalSumSquares / errorSumSquares);
            snrDb.Should().BeGreaterThan(60, "a pure MDCT/IMDCT TDAC round trip with no quantization, alias reduction, or polyphase filtering involved at all should reconstruct the original windowed block essentially exactly");
        }
    }
}
