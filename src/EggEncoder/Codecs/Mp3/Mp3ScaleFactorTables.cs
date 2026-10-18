namespace EggEncoder.Codecs.Mp3
{
    // ISO/IEC 11172-3 Annex tables needed to decode scalefactors and requantize -- consulted
    // directly from the published standard's own bitstream-syntax description (the same
    // "MPEG-Layer3 Bitstream Syntax and Decoding" reference document, and independent academic
    // cross-checks, Mp3Tables.cs's own tables were already sourced from), not from any
    // encoder/decoder implementation's source. Scoped to MPEG-1 only (32000/44100/48000 Hz) --
    // MPEG-2/2.5 (LSF) has its own, different scalefactor-compression convention entirely, out of
    // scope for this item (see docs/external-dependency-removal-plan.md Phase 1, sub-item 1d).
    internal static class Mp3ScaleFactorTables
    {
        // scalefac_compress[gr][ch] (0-15) -> (slen1, slen2): the number of bits used to transmit
        // each scalefactor in the first/second group of scalefactor bands respectively, for a long
        // (or start/stop) block. Table 20's own title is "Definition of scalefac_multiplier", but
        // this specific 16-entry slen1/slen2 table is the bitstream-syntax document's own Table 16
        // ("Size of slen1[gr][ch] and slen2[gr][ch]") -- confirmed as a direct 1:1 transcription of
        // that table's own rows, cross-checked independently against a second, unrelated published
        // source before being relied on.
        public static readonly (int Slen1, int Slen2)[] ScalefacCompressTable =
        [
            (0, 0), (0, 1), (0, 2), (0, 3),
            (3, 0), (1, 1), (1, 2), (1, 3),
            (2, 1), (2, 2), (2, 3), (3, 1),
            (3, 2), (3, 3), (4, 2), (4, 3)
        ];

        // scalefac_scale[gr][ch] -> scalefac_multiplier, used directly in the requantization
        // formula (section 4.3.9) as a standalone 2^(-scalefac_multiplier*scalefac) factor:
        // 0.5 when scalefac_scale is '0' (a scalefactor step of 1 in log2-quarter-power units),
        // 1.0 when it's '1' (a step of 2) -- the bitstream-syntax document's own Table 20.
        public static double ScalefacMultiplier(bool scalefacScale) => scalefacScale ? 1.0 : 0.5;

        // Table 23 "Preemphasis (pretab)" -- added to scalefac_l[sfb] (not scaled by
        // scalefac_multiplier; see Mp3Requantizer's own doc comment) when preflag is set, for long
        // (and start/stop) blocks only. 22 entries, sfb 0-21; the document's own table only lists
        // non-zero/explicit entries through sfb 20 (sfb 21 -- the very top of the spectrum -- is
        // implicitly 0, the same "scalefactor for frequency lines above the highest tabulated line
        // is zero" convention the document states generally for scalefactors themselves).
        public static readonly int[] Pretab =
        [
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 3, 3, 3, 2, 0
        ];

        // Long-block scalefactor band boundaries (index_of_start/index_of_end, both inclusive) for
        // the three MPEG-1 sample rates this project supports -- Tables 54/56/58 of the bitstream-
        // syntax document, 21 explicit bands (sfb 0-20) each; frequency lines past the last band's
        // own index_of_end implicitly carry scalefactor 0, the same convention Pretab's own trailing
        // entry follows. Each entry is (IndexOfStart, IndexOfEnd).
        public static readonly (int IndexOfStart, int IndexOfEnd)[] LongBlockBands32000Hz =
        [
            (0, 3), (4, 7), (8, 11), (12, 15), (16, 19), (20, 23), (24, 29), (30, 35), (36, 43),
            (44, 53), (54, 65), (66, 81), (82, 101), (102, 125), (126, 155), (156, 193), (194, 239),
            (240, 295), (296, 363), (364, 447), (448, 549)
        ];

        public static readonly (int IndexOfStart, int IndexOfEnd)[] LongBlockBands44100Hz =
        [
            (0, 3), (4, 7), (8, 11), (12, 15), (16, 19), (20, 23), (24, 29), (30, 35), (36, 43),
            (44, 51), (52, 61), (62, 73), (74, 89), (90, 109), (110, 133), (134, 161), (162, 195),
            (196, 237), (238, 287), (288, 341), (342, 417)
        ];

        public static readonly (int IndexOfStart, int IndexOfEnd)[] LongBlockBands48000Hz =
        [
            (0, 3), (4, 7), (8, 11), (12, 15), (16, 19), (20, 23), (24, 29), (30, 35), (36, 41),
            (42, 49), (50, 59), (60, 71), (72, 87), (88, 105), (106, 127), (128, 155), (156, 189),
            (190, 229), (230, 275), (276, 329), (330, 383)
        ];

        public static (int IndexOfStart, int IndexOfEnd)[] GetLongBlockBands(int sampleRate) => sampleRate switch
        {
            32000 => LongBlockBands32000Hz,
            44100 => LongBlockBands44100Hz,
            48000 => LongBlockBands48000Hz,
            _ => throw new NotSupportedException($"MP3 decoding only supports 32000/44100/48000 Hz long-block scalefactor bands, not {sampleRate} Hz"),
        };
    }
}
