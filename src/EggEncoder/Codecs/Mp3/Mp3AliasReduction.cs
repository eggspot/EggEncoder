namespace EggEncoder.Codecs.Mp3
{
    // The spec's own decode-side alias reduction (section 4.3.12.1's published pseudo code) computes,
    // for each adjacent subband pair sb-1/sb and i in 0..7:
    //   xar[18*sb-1-i] = xr[18*sb-1-i]*Cs[i] - xr[18*sb+i]*Ca[i]
    //   xar[18*sb+i]   = xr[18*sb+i]*Cs[i]   + xr[18*sb-1-i]*Ca[i]
    // applied to the dequantized values (xr) before the IMDCT. Cs/Ca form an orthogonal 2x2 rotation
    // per i (Cs^2+Ca^2==1 by construction, since Ca=C/sqrt(1+C^2) and Cs=1/sqrt(1+C^2)), so the
    // exact inverse is that same rotation's transpose. This encoder applies that inverse to its own
    // raw (pre-alias-reduction) MDCT output to produce the values it actually quantizes and
    // transmits -- so that a conformant decoder's own forward butterfly above reconstructs exactly
    // this encoder's raw MDCT output before its own IMDCT, the same "derive the encoder step as the
    // mathematical inverse of the spec's own decode step" approach this project's WavPack encoder
    // already used for its own decorrelation/entropy formulas.
    internal static class Mp3AliasReduction
    {
        private const int SubbandCount = 32;
        private const int CoefficientsPerSubband = 18;
        private const int ButterflyWidth = 8;

        public static void Encode(double[] xr)
        {
            if (xr.Length != SubbandCount * CoefficientsPerSubband)
            {
                throw new ArgumentException($"Alias reduction expects exactly {SubbandCount * CoefficientsPerSubband} coefficients", nameof(xr));
            }

            for (var sb = 1; sb < SubbandCount; sb++)
            {
                for (var i = 0; i < ButterflyWidth; i++)
                {
                    var lowerIndex = (CoefficientsPerSubband * sb) - 1 - i;
                    var upperIndex = (CoefficientsPerSubband * sb) + i;

                    var xarLower = xr[lowerIndex];
                    var xarUpper = xr[upperIndex];

                    var cs = Mp3Tables.AliasCs[i];
                    var ca = Mp3Tables.AliasCa[i];

                    xr[lowerIndex] = (xarLower * cs) + (xarUpper * ca);
                    xr[upperIndex] = (xarUpper * cs) - (xarLower * ca);
                }
            }
        }

        // The spec's own decode-side formula, applied as-is (unlike Encode above, which applies its
        // mathematical inverse) -- see this class's own doc comment for the exact pseudo code this
        // implements verbatim. Long blocks only (sb runs the full 1..31 range); short/mixed blocks
        // apply no alias reduction at all per the spec (a block-switching-only concern, out of scope
        // for Mp3FrameDecoder's own long-block-only baseline -- see docs/external-dependency-removal-plan.md
        // Phase 1 sub-item 1c).
        public static void Decode(double[] xr)
        {
            if (xr.Length != SubbandCount * CoefficientsPerSubband)
            {
                throw new ArgumentException($"Alias reduction expects exactly {SubbandCount * CoefficientsPerSubband} coefficients", nameof(xr));
            }

            for (var sb = 1; sb < SubbandCount; sb++)
            {
                for (var i = 0; i < ButterflyWidth; i++)
                {
                    var lowerIndex = (CoefficientsPerSubband * sb) - 1 - i;
                    var upperIndex = (CoefficientsPerSubband * sb) + i;

                    var xrLower = xr[lowerIndex];
                    var xrUpper = xr[upperIndex];

                    var cs = Mp3Tables.AliasCs[i];
                    var ca = Mp3Tables.AliasCa[i];

                    xr[lowerIndex] = (xrLower * cs) - (xrUpper * ca);
                    xr[upperIndex] = (xrUpper * cs) + (xrLower * ca);
                }
            }
        }
    }
}
