namespace EggEncoder.Media
{
    /// <summary>
    /// Parses the header fields of an H.265 sequence parameter set (ITU-T H.265 clause 7.3.2.2.1)
    /// up to and including <c>bit_depth_chroma_minus8</c>: general profile/tier/level, chroma
    /// format, conformance-window-cropped display size, bit depth. Stops there; nothing after it,
    /// and no slice, is read. See docs/live-ingest-restream-plan.md item 1.
    /// </summary>
    public static class H265SpsParser
    {
        private const int SpsNalUnitType = 33;
        private const string What = "H.265 SPS";

        /// <summary>Parses one SPS NAL unit, including its two-byte NAL header (as stored in hvcC).</summary>
        /// <exception cref="InvalidDataException">Not an SPS NAL unit, truncated, or a field out of its spec range.</exception>
        public static H265SpsInfo Parse(ReadOnlySpan<byte> spsNalUnit)
        {
            if (spsNalUnit.Length < 3)
            {
                throw new InvalidDataException($"{What} is truncated: {spsNalUnit.Length} bytes");
            }

            var nalUnitType = (spsNalUnit[0] >> 1) & 0x3F;
            if (nalUnitType != SpsNalUnitType)
            {
                throw new InvalidDataException($"NAL unit type {nalUnitType} is not an {What} (type 33)");
            }

            var reader = new RbspReader(spsNalUnit, headerLength: 2, What);
            reader.SkipBits(4); // sps_video_parameter_set_id
            var maxSubLayersMinus1 = (int)reader.ReadBits(3);
            if (maxSubLayersMinus1 > 6)
            {
                throw new InvalidDataException($"{What}: sps_max_sub_layers_minus1 is {maxSubLayersMinus1}, above its maximum of 6");
            }

            reader.SkipBits(1); // sps_temporal_id_nesting_flag
            var profileTierLevel = ReadProfileTierLevel(reader, maxSubLayersMinus1);

            reader.ReadUnsignedExpGolomb(15, "sps_seq_parameter_set_id");
            var chromaFormatIdc = reader.ReadUnsignedExpGolomb(3, "chroma_format_idc");
            var separateColourPlane = chromaFormatIdc == 3 && reader.ReadFlag();

            var width = (long)reader.ReadUnsignedExpGolomb(); // pic_width_in_luma_samples
            var height = (long)reader.ReadUnsignedExpGolomb(); // pic_height_in_luma_samples

            if (reader.ReadFlag()) // conformance_window_flag
            {
                // Clause 7.4.3.2.1: offsets are in chroma sample units (SubWidthC/SubHeightC),
                // which are 1 for 4:4:4 and for separately coded colour planes.
                var chromaArrayType = separateColourPlane ? 0 : chromaFormatIdc;
                var subWidthC = chromaArrayType is 1 or 2 ? 2 : 1;
                var subHeightC = chromaArrayType == 1 ? 2 : 1;

                var left = (long)reader.ReadUnsignedExpGolomb();
                var right = (long)reader.ReadUnsignedExpGolomb();
                var top = (long)reader.ReadUnsignedExpGolomb();
                var bottom = (long)reader.ReadUnsignedExpGolomb();

                width -= subWidthC * (left + right);
                height -= subHeightC * (top + bottom);
            }

            var bitDepthLuma = 8 + reader.ReadUnsignedExpGolomb(8, "bit_depth_luma_minus8");
            var bitDepthChroma = 8 + reader.ReadUnsignedExpGolomb(8, "bit_depth_chroma_minus8");

            if (width is <= 0 or > int.MaxValue || height is <= 0 or > int.MaxValue)
            {
                throw new InvalidDataException($"{What}: invalid picture size {width}x{height}");
            }

            return new H265SpsInfo(profileTierLevel, chromaFormatIdc, bitDepthLuma, bitDepthChroma, (int)width, (int)height);
        }

        // Clause 7.3.3 with profilePresentFlag = 1: the general fields, then each sub-layer's
        // optional profile (88 bits) and level (8 bits), which are skipped.
        private static HevcProfileTierLevel ReadProfileTierLevel(RbspReader reader, int maxSubLayersMinus1)
        {
            var profileSpace = (int)reader.ReadBits(2);
            var tierFlag = reader.ReadFlag();
            var profileIdc = (int)reader.ReadBits(5);
            var compatibilityFlags = reader.ReadBits(32);
            var constraintFlags = ((ulong)reader.ReadBits(16) << 32) | reader.ReadBits(32);
            var levelIdc = (int)reader.ReadBits(8);

            Span<bool> subLayerProfilePresent = stackalloc bool[7];
            Span<bool> subLayerLevelPresent = stackalloc bool[7];
            for (var i = 0; i < maxSubLayersMinus1; i++)
            {
                subLayerProfilePresent[i] = reader.ReadFlag();
                subLayerLevelPresent[i] = reader.ReadFlag();
            }

            if (maxSubLayersMinus1 > 0)
            {
                reader.SkipBits(2 * (8 - maxSubLayersMinus1)); // reserved_zero_2bits
            }

            for (var i = 0; i < maxSubLayersMinus1; i++)
            {
                if (subLayerProfilePresent[i])
                {
                    reader.SkipBits(88);
                }

                if (subLayerLevelPresent[i])
                {
                    reader.SkipBits(8);
                }
            }

            return new HevcProfileTierLevel(profileSpace, tierFlag, profileIdc, compatibilityFlags, constraintFlags, levelIdc);
        }
    }
}
