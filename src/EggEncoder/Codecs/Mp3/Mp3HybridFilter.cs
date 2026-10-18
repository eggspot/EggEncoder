using EggEncoder.Transform;

namespace EggEncoder.Codecs.Mp3
{
    // The decode-side hybrid filter: per-subband IMDCT + windowing + overlap-add, the mirror image
    // of Mp3FrameEncoder.ComputeGranuleCoefficients' own per-subband MDCT step. Long blocks
    // ("normal" window) only -- short/mixed blocks need the three-short-window shape instead (see
    // docs/external-dependency-removal-plan.md Phase 1 sub-item 1c). One instance must be reused
    // across consecutive granules of the SAME channel (it holds the 50%-overlap state), the same
    // "stateful, build fresh per channel" shape Mp3PolyphaseFilter/Mp3SynthesisFilter already use.
    internal sealed class Mp3HybridFilter
    {
        private const int SubbandCount = 32;
        private const int CoefficientsPerSubband = 18;
        private const int BlockSize = 36;

        private static readonly double[] _window = BuildSineWindow();

        private readonly double[][] _overlap;

        public Mp3HybridFilter()
        {
            _overlap = new double[SubbandCount][];
            for (var sb = 0; sb < SubbandCount; sb++)
            {
                _overlap[sb] = new double[CoefficientsPerSubband];
            }
        }

        /// <summary>
        /// <paramref name="xr"/> is one granule/channel's 576 dequantized, alias-reduction-decoded
        /// spectral values, subband-major (<c>xr[sb*18+k]</c>). Returns 576 time-domain samples,
        /// also subband-major (<c>output[sb*18+t]</c> is subband <c>sb</c>'s own <c>t</c>'th time
        /// sample within this granule) -- ready for frequency inversion and the synthesis filter,
        /// 18 ticks of 32 subband samples each (transposed from this subband-major layout).
        /// </summary>
        public double[] Process(double[] xr)
        {
            if (xr.Length != SubbandCount * CoefficientsPerSubband)
            {
                throw new ArgumentException($"Hybrid filter expects exactly {SubbandCount * CoefficientsPerSubband} coefficients", nameof(xr));
            }

            var output = new double[SubbandCount * CoefficientsPerSubband];
            var coefficients = new double[CoefficientsPerSubband];

            for (var sb = 0; sb < SubbandCount; sb++)
            {
                Array.Copy(xr, sb * CoefficientsPerSubband, coefficients, 0, CoefficientsPerSubband);

                var timeDomain = Mdct.Inverse(coefficients);

                // Transform.Mdct.Inverse divides its own cosine sum by CoefficientsPerSubband (18)
                // -- a normalization convention a real decoder's own IMDCT does NOT apply (this
                // exact fact, and the exact factor, is already independently confirmed and
                // documented in Mp3FrameEncoder.ComputeGranuleCoefficients' own comment, which
                // compensates for it on the ENCODE side by pre-dividing its own forward-MDCT
                // output by this same 18 before quantizing -- so a real/this-project's-own decoder
                // reconstructing that encoded data is expected to apply an UN-normalized IMDCT).
                // Multiplying back here makes Mdct.Inverse behave like that real decoder's own
                // IMDCT instead of its own default (AAC/WMA-tailored) normalization.
                //
                // The further *2 corrects a second, independently confirmed factor: applying this
                // same sine window on BOTH the forward (encode) and inverse (here) sides of the
                // TDAC overlap-add halves the reconstructed amplitude relative to the original
                // signal, verified directly via a minimal from-scratch Python harness reproducing
                // Transform.Mdct's exact formula and this window (a constant input reconstructs to
                // exactly 0.5x with only the *18 compensation, and exactly 1.0x -- bit-exact -- once
                // this *2 is included). This is an inherent property of this "n+0.5+K/2"-basis
                // TDAC-MDCT convention when the same window is applied on both the forward and
                // inverse sides, not something specific to MP3 -- it just has no reason to show up
                // in Transform.Mdct's own existing AAC/WMA usage, where an arbitrary constant gain
                // is absorbed into those formats' own adaptive, self-consistent scalefactor
                // quantization rather than needing to match an external, independently-specified
                // decoder's exact amplitude the way MP3 (a format this decoder must also decode
                // real-world, non-self-produced files for) does.
                for (var n = 0; n < BlockSize; n++)
                {
                    timeDomain[n] *= CoefficientsPerSubband * 2.0;
                }

                for (var n = 0; n < BlockSize; n++)
                {
                    timeDomain[n] *= _window[n];
                }

                var baseIndex = sb * CoefficientsPerSubband;
                for (var n = 0; n < CoefficientsPerSubband; n++)
                {
                    output[baseIndex + n] = timeDomain[n] + _overlap[sb][n];
                }

                Array.Copy(timeDomain, CoefficientsPerSubband, _overlap[sb], 0, CoefficientsPerSubband);
            }

            return output;
        }

        // The spec's own "normal window" shape for a long block (section 4.3.12.3): a plain
        // 36-point sine window, the same family AacFrameDecoder's own BuildSineWindow already uses
        // for its (differently-sized) long-block window -- confirmed independently via the
        // "DIGITAL IC-PROJECT, LTH, SWEDEN, MAY 2003" academic report's own IMDCT description,
        // which this project's existing Transform.Mdct.Inverse was independently cross-checked
        // against formula-for-formula (see Mp3Requantizer.cs's own doc comment).
        private static double[] BuildSineWindow()
        {
            var window = new double[BlockSize];
            for (var n = 0; n < BlockSize; n++)
            {
                window[n] = Math.Sin(Math.PI / BlockSize * (n + 0.5));
            }

            return window;
        }
    }
}
