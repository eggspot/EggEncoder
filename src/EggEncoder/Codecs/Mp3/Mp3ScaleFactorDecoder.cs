using EggEncoder.Transform;

namespace EggEncoder.Codecs.Mp3
{
    // Decodes one granule/channel's long-block scalefac_l values from the bitstream -- the
    // bitstream-syntax document's own section 4.2.4 ("Scalefactors") / Table 5 ("Bitstream syntax
    // of main_data") "if(ID=='1') ... else{ for(sfb=0;sfb<21;sfb++) scalefac_l[gr][ch][sfb] }"
    // structure. Short/mixed blocks need four separately-sized partitions (nr_of_sfb1..4) instead
    // of this flat 21-band layout -- out of scope here (see
    // docs/external-dependency-removal-plan.md Phase 1 sub-item 1c).
    internal static class Mp3ScaleFactorDecoder
    {
        private const int LongBlockBandCount = 21; // sfb 0-20

        /// <summary>
        /// <paramref name="scfsi"/> is the 4-entry scfsi[ch][0..3] array from this channel's own
        /// side info (meaningless for <paramref name="granule"/> 0, which always reads every band
        /// fresh regardless of scfsi). <paramref name="granule0Scalefactors"/> is required (and
        /// used) only when decoding granule 1 and the relevant scfsi band is set -- those bands'
        /// own scalefactors are copied from granule 0 rather than read from the bitstream at all,
        /// per Table 14's own "scalefactors transmitted for granule0 are also valid for granule1"
        /// rule.
        /// </summary>
        public static int[] DecodeLongBlock(BitReader reader, int scalefacCompress, int granule, bool[] scfsi, int[]? granule0Scalefactors)
        {
            var (slen1, slen2) = Mp3ScaleFactorTables.ScalefacCompressTable[scalefacCompress];
            var scalefactors = new int[LongBlockBandCount];

            for (var sfb = 0; sfb < LongBlockBandCount; sfb++)
            {
                var bits = sfb <= 10 ? slen1 : slen2;
                var scfsiGroup = ScfsiGroupForSfb(sfb);

                if (granule == 1 && scfsi[scfsiGroup])
                {
                    scalefactors[sfb] = granule0Scalefactors![sfb];
                }
                else
                {
                    scalefactors[sfb] = (int)reader.ReadBits(bits);
                }
            }

            return scalefactors;
        }

        // Table 15 "Attachment of scalefactor bands to groups": band0 = sfb 0-5, band1 = sfb 6-10,
        // band2 = sfb 11-15, band3 = sfb 16-20.
        private static int ScfsiGroupForSfb(int sfb) => sfb switch
        {
            <= 5 => 0,
            <= 10 => 1,
            <= 15 => 2,
            _ => 3,
        };
    }
}
