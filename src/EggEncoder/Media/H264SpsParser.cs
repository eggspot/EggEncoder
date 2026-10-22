namespace EggEncoder.Media
{
    /// <summary>
    /// Parses the header fields of an H.264 sequence parameter set (ITU-T H.264 clause 7.3.2.1.1)
    /// up to and including <c>frame_cropping</c>: profile, level, chroma format, bit depth and
    /// cropped display size. Stops there; VUI, and every slice, is never read. See
    /// docs/live-ingest-restream-plan.md item 1.
    /// </summary>
    public static class H264SpsParser
    {
        private const int SpsNalUnitType = 7;
        private const string What = "H.264 SPS";

        /// <summary>Parses one SPS NAL unit, including its one-byte NAL header (as stored in avcC).</summary>
        /// <exception cref="InvalidDataException">Not an SPS NAL unit, truncated, or a field out of its spec range.</exception>
        public static H264SpsInfo Parse(ReadOnlySpan<byte> spsNalUnit)
        {
            if (spsNalUnit.Length < 4)
            {
                throw new InvalidDataException($"{What} is truncated: {spsNalUnit.Length} bytes");
            }

            if ((spsNalUnit[0] & 0x1F) != SpsNalUnitType)
            {
                throw new InvalidDataException($"NAL unit type {spsNalUnit[0] & 0x1F} is not an {What} (type 7)");
            }

            var reader = new RbspReader(spsNalUnit, headerLength: 1, What);
            var profileIdc = (int)reader.ReadBits(8);
            var constraintFlags = (int)reader.ReadBits(8);
            var levelIdc = (int)reader.ReadBits(8);
            reader.ReadUnsignedExpGolomb(31, "seq_parameter_set_id");

            var chromaFormatIdc = 1;
            var separateColourPlane = false;
            var bitDepthLuma = 8;
            var bitDepthChroma = 8;
            if (HasChromaFormatFields(profileIdc))
            {
                chromaFormatIdc = reader.ReadUnsignedExpGolomb(3, "chroma_format_idc");
                if (chromaFormatIdc == 3)
                {
                    separateColourPlane = reader.ReadFlag();
                }

                bitDepthLuma = 8 + reader.ReadUnsignedExpGolomb(6, "bit_depth_luma_minus8");
                bitDepthChroma = 8 + reader.ReadUnsignedExpGolomb(6, "bit_depth_chroma_minus8");
                reader.SkipBits(1); // qpprime_y_zero_transform_bypass_flag

                if (reader.ReadFlag()) // seq_scaling_matrix_present_flag
                {
                    var listCount = chromaFormatIdc == 3 ? 12 : 8;
                    for (var i = 0; i < listCount; i++)
                    {
                        if (reader.ReadFlag()) // seq_scaling_list_present_flag[i]
                        {
                            SkipScalingList(reader, i < 6 ? 16 : 64);
                        }
                    }
                }
            }

            reader.ReadUnsignedExpGolomb(12, "log2_max_frame_num_minus4");
            var picOrderCntType = reader.ReadUnsignedExpGolomb(2, "pic_order_cnt_type");
            if (picOrderCntType == 0)
            {
                reader.ReadUnsignedExpGolomb(12, "log2_max_pic_order_cnt_lsb_minus4");
            }
            else if (picOrderCntType == 1)
            {
                reader.SkipBits(1); // delta_pic_order_always_zero_flag
                reader.ReadSignedExpGolomb(); // offset_for_non_ref_pic
                reader.ReadSignedExpGolomb(); // offset_for_top_to_bottom_field
                var cycleLength = reader.ReadUnsignedExpGolomb(255, "num_ref_frames_in_pic_order_cnt_cycle");
                for (var i = 0; i < cycleLength; i++)
                {
                    reader.ReadSignedExpGolomb(); // offset_for_ref_frame[i]
                }
            }

            reader.ReadUnsignedExpGolomb(); // max_num_ref_frames
            reader.SkipBits(1); // gaps_in_frame_num_value_allowed_flag
            var widthInMbs = (long)reader.ReadUnsignedExpGolomb() + 1;
            var heightInMapUnits = (long)reader.ReadUnsignedExpGolomb() + 1;
            var frameMbsOnly = reader.ReadFlag();
            if (!frameMbsOnly)
            {
                reader.SkipBits(1); // mb_adaptive_frame_field_flag
            }

            reader.SkipBits(1); // direct_8x8_inference_flag

            var width = widthInMbs * 16;
            var height = (frameMbsOnly ? 1 : 2) * heightInMapUnits * 16;

            if (reader.ReadFlag()) // frame_cropping_flag
            {
                // Clause 7.4.2.1.1: crop offsets are in chroma sample units (luma units when
                // ChromaArrayType is 0), doubled vertically for field-coded streams.
                var chromaArrayType = separateColourPlane ? 0 : chromaFormatIdc;
                var cropUnitX = chromaArrayType == 0 ? 1 : (chromaFormatIdc == 3 ? 1 : 2);
                var cropUnitY = (chromaArrayType == 0 ? 1 : (chromaFormatIdc == 1 ? 2 : 1)) * (frameMbsOnly ? 1 : 2);

                var left = (long)reader.ReadUnsignedExpGolomb();
                var right = (long)reader.ReadUnsignedExpGolomb();
                var top = (long)reader.ReadUnsignedExpGolomb();
                var bottom = (long)reader.ReadUnsignedExpGolomb();

                width -= cropUnitX * (left + right);
                height -= cropUnitY * (top + bottom);
            }

            if (width is <= 0 or > int.MaxValue || height is <= 0 or > int.MaxValue)
            {
                throw new InvalidDataException($"{What}: invalid picture size {width}x{height}");
            }

            return new H264SpsInfo(profileIdc, constraintFlags, levelIdc, chromaFormatIdc, bitDepthLuma, bitDepthChroma, (int)width, (int)height);
        }

        // Clause 7.3.2.1.1: the profiles whose SPS carries chroma_format_idc and the fields after it.
        private static bool HasChromaFormatFields(int profileIdc) =>
            profileIdc is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135;

        // Clause 7.3.2.1.1.1: consumes one scaling_list() without keeping it.
        private static void SkipScalingList(RbspReader reader, int size)
        {
            var lastScale = 8;
            var nextScale = 8;
            for (var j = 0; j < size && nextScale != 0; j++)
            {
                var deltaScale = reader.ReadSignedExpGolomb();
                nextScale = (int)(((lastScale + (long)deltaScale) % 256 + 256) % 256);
                lastScale = nextScale == 0 ? lastScale : nextScale;
            }
        }
    }
}
