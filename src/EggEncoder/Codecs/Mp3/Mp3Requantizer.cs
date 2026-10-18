namespace EggEncoder.Codecs.Mp3
{
    // The decode-side inverse of Mp3Quantizer: converts Huffman-decoded integer magnitudes (is[i])
    // back into real spectral values (xr[i]), per the bitstream-syntax document's own section 4.3.9
    // "Requantization and Rescaling" formula -- cross-validated against an independent academic
    // source (a 2003 Lund Institute of Technology FPGA-decoder report, which renders the same
    // formula as real extractable text where the primary spec-syntax document's own copy rendered
    // as a blank embedded equation image) and against this project's own already-shipped encoder
    // comment (Mp3Quantizer.cs's own "xr(i) = sign(is(i)) * |is(i)|^(4/3) * 2^((global_gain-210)/4)"
    // for the magnitude/global_gain relationship specifically). Long blocks only -- short/mixed
    // blocks need an additional subblock_gain term this class doesn't implement (see
    // docs/external-dependency-removal-plan.md Phase 1 sub-item 1c):
    //
    //   xr[i] = sign(is[i]) * |is[i]|^(4/3)
    //           * 2^((global_gain - 210) / 4)
    //           * 2^(-scalefac_multiplier * scalefac_l[sfb])
    //           * 2^(-preflag * pretab[sfb])
    //
    // where scalefac_multiplier is 0.5 or 1.0 depending on scalefac_scale (Mp3ScaleFactorTables.
    // ScalefacMultiplier) -- NOT itself scaled by the preflag/pretab term, which the spec's own
    // formula display shows as a separate, unscaled 2^(-preflag*pretab[sfb]) factor.
    internal static class Mp3Requantizer
    {
        private const int CoefficientCount = 576;
        private const double GainBase = 210.0;

        /// <summary>
        /// Dequantizes one granule/channel's 576 Huffman-decoded integers into real spectral
        /// values, using <paramref name="scalefactorsBySfb"/> (one entry per long-block
        /// scalefactor band, 21 entries for sfb 0-20 -- see <see cref="Mp3ScaleFactorTables"/>) and
        /// <paramref name="bands"/> (that same sample rate's own scalefactor band boundaries) to
        /// know which band each frequency line belongs to. A frequency line past the last
        /// tabulated band's own <c>IndexOfEnd</c> uses scalefactor 0 and no preemphasis, the same
        /// "implicitly zero above the highest tabulated line" convention the scalefactor bands and
        /// <see cref="Mp3ScaleFactorTables.Pretab"/> both already follow.
        /// </summary>
        public static double[] Decode(int[] quantized, int[] scalefactorsBySfb, (int IndexOfStart, int IndexOfEnd)[] bands, int globalGain, bool preflag, bool scalefacScale)
        {
            if (quantized.Length != CoefficientCount)
            {
                throw new ArgumentException($"Expected exactly {CoefficientCount} quantized values", nameof(quantized));
            }

            var xr = new double[CoefficientCount];
            var globalScale = Math.Pow(2.0, (globalGain - GainBase) / 4.0);
            var scalefacMultiplier = Mp3ScaleFactorTables.ScalefacMultiplier(scalefacScale);

            var sfb = 0;
            for (var i = 0; i < CoefficientCount; i++)
            {
                while (sfb < bands.Length - 1 && i > bands[sfb].IndexOfEnd)
                {
                    sfb++;
                }

                var quantizedValue = quantized[i];
                if (quantizedValue == 0)
                {
                    continue;
                }

                var withinBand = sfb < bands.Length && i >= bands[sfb].IndexOfStart && i <= bands[sfb].IndexOfEnd;
                var scalefac = withinBand ? scalefactorsBySfb[sfb] : 0;
                var pretabValue = preflag && withinBand ? Mp3ScaleFactorTables.Pretab[sfb] : 0;

                var scaleExponent = -(scalefacMultiplier * scalefac) - pretabValue;
                var scale = globalScale * Math.Pow(2.0, scaleExponent);

                var magnitude = Math.Pow(Math.Abs(quantizedValue), 4.0 / 3.0) * scale;
                xr[i] = quantizedValue < 0 ? -magnitude : magnitude;
            }

            return xr;
        }
    }
}
