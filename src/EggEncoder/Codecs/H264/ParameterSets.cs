using EggEncoder.Video;

namespace EggEncoder.Codecs.H264;

internal sealed class SequenceParameterSet
{
    private const int MaxMacroblocksPerSide = 1024;

    public int ProfileIdc { get; private init; }

    public int LevelIdc { get; private init; }

    public int Id { get; private init; }

    public int ChromaFormatIdc { get; private init; } = 1;

    public bool HasScalingMatrix { get; private init; }

    public byte[][] ScalingLists { get; private init; } = H264.ScalingLists.FlatSet();

    public int Log2MaxFrameNum { get; private init; }

    public int PicOrderCountType { get; private init; }

    public int Log2MaxPicOrderCountLsb { get; private init; }

    public bool DeltaPicOrderAlwaysZero { get; private init; }

    public int OffsetForNonReferencePicture { get; private init; }

    public int OffsetForTopToBottomField { get; private init; }

    public int[] OffsetForReferenceFrame { get; private init; } = [];

    public int MaxNumberOfReferenceFrames { get; private init; }

    public bool GapsInFrameNumAllowed { get; private init; }

    public int WidthInMacroblocks { get; private init; }

    public int HeightInMacroblocks { get; private init; }

    public bool Direct8x8Inference { get; private init; }

    public int CropLeft { get; private init; }

    public int CropRight { get; private init; }

    public int CropTop { get; private init; }

    public int CropBottom { get; private init; }

    public VideoColorRange Range { get; private init; }

    public VideoColorMatrix? Matrix { get; private init; }

    public VideoFrameRate? FrameRate { get; private init; }

    public int? MaxNumberOfReorderFrames { get; private init; }

    public int? MaxDecodedFrameBuffering { get; private init; }

    public int Width => (WidthInMacroblocks * 16) - (2 * (CropLeft + CropRight));

    public int Height => (HeightInMacroblocks * 16) - (2 * (CropTop + CropBottom));

    public int MacroblockCount => WidthInMacroblocks * HeightInMacroblocks;

    public VideoColorMatrix ColorMatrix => Matrix ?? (Height < 720 ? VideoColorMatrix.Bt601 : VideoColorMatrix.Bt709);

    public int DecodedPictureBufferSize
    {
        get
        {
            var fromLevel = Math.Clamp(MaxDpbMacroblocks(LevelIdc) / Math.Max(1, MacroblockCount), 1, 16);
            return Math.Clamp(Math.Max(MaxDecodedFrameBuffering ?? fromLevel, MaxNumberOfReferenceFrames), 1, 16);
        }
    }

    public static SequenceParameterSet Parse(H264BitReader reader)
    {
        var profile = (int)reader.ReadBits(8);
        reader.SkipBits(8);
        var level = (int)reader.ReadBits(8);
        var id = (int)reader.ReadUe(31, "seq_parameter_set_id");
        var chromaFormat = 1;
        var hasMatrix = false;
        var lists = H264.ScalingLists.FlatSet();
        if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
        {
            chromaFormat = (int)reader.ReadUe(3, "chroma_format_idc");
            if (chromaFormat == 3)
            {
                reader.SkipBits(1);
            }

            var lumaDepth = reader.ReadUe(6, "bit_depth_luma_minus8") + 8;
            var chromaDepth = reader.ReadUe(6, "bit_depth_chroma_minus8") + 8;
            if (lumaDepth != 8 || chromaDepth != 8)
            {
                throw new NotSupportedException($"H.264: {lumaDepth}-bit video is not supported; only 8-bit");
            }

            if (reader.ReadFlag())
            {
                throw new NotSupportedException("H.264: lossless (qpprime_y_zero_transform_bypass) is not supported");
            }

            hasMatrix = reader.ReadFlag();
            if (hasMatrix)
            {
                lists = H264.ScalingLists.Resolve(H264.ScalingLists.ReadExplicit(reader, chromaFormat != 3 ? 8 : 12), null);
            }
        }

        if (chromaFormat != 1)
        {
            throw new NotSupportedException($"H.264: chroma format {chromaFormat} is not supported; only 4:2:0");
        }

        var log2MaxFrameNum = (int)reader.ReadUe(12, "log2_max_frame_num_minus4") + 4;
        var pocType = (int)reader.ReadUe(2, "pic_order_cnt_type");
        var log2MaxPocLsb = 0;
        var alwaysZero = false;
        var offsetNonRef = 0;
        var offsetTopBottom = 0;
        var offsets = Array.Empty<int>();
        if (pocType == 0)
        {
            log2MaxPocLsb = (int)reader.ReadUe(12, "log2_max_pic_order_cnt_lsb_minus4") + 4;
        }
        else if (pocType == 1)
        {
            alwaysZero = reader.ReadFlag();
            offsetNonRef = reader.ReadSe();
            offsetTopBottom = reader.ReadSe();
            offsets = new int[reader.ReadUe(255, "num_ref_frames_in_pic_order_cnt_cycle")];
            for (var i = 0; i < offsets.Length; i++)
            {
                offsets[i] = reader.ReadSe();
            }
        }

        var maxRefFrames = (int)reader.ReadUe(16, "max_num_ref_frames");
        var gaps = reader.ReadFlag();
        var widthInMbs = (int)reader.ReadUe(MaxMacroblocksPerSide - 1, "pic_width_in_mbs_minus1") + 1;
        var heightInMapUnits = (int)reader.ReadUe(MaxMacroblocksPerSide - 1, "pic_height_in_map_units_minus1") + 1;
        if (!reader.ReadFlag())
        {
            throw new NotSupportedException("H.264: interlaced video (field or MBAFF coding) is not supported");
        }

        var direct8x8 = reader.ReadFlag();
        int cropLeft = 0, cropRight = 0, cropTop = 0, cropBottom = 0;
        if (reader.ReadFlag())
        {
            cropLeft = (int)reader.ReadUe(8 * MaxMacroblocksPerSide, "frame_crop_left_offset");
            cropRight = (int)reader.ReadUe(8 * MaxMacroblocksPerSide, "frame_crop_right_offset");
            cropTop = (int)reader.ReadUe(8 * MaxMacroblocksPerSide, "frame_crop_top_offset");
            cropBottom = (int)reader.ReadUe(8 * MaxMacroblocksPerSide, "frame_crop_bottom_offset");
            if (2 * (cropLeft + cropRight) >= widthInMbs * 16 || 2 * (cropTop + cropBottom) >= heightInMapUnits * 16)
            {
                throw new InvalidDataException("H.264: the frame cropping removes the whole picture");
            }
        }

        var range = VideoColorRange.Limited;
        VideoColorMatrix? matrix = null;
        VideoFrameRate? rate = null;
        int? reorder = null;
        int? dpb = null;
        if (reader.ReadFlag())
        {
            ReadVui(reader, ref range, ref matrix, ref rate, ref reorder, ref dpb);
        }

        return new SequenceParameterSet
        {
            ProfileIdc = profile,
            LevelIdc = level,
            Id = id,
            ChromaFormatIdc = chromaFormat,
            HasScalingMatrix = hasMatrix,
            ScalingLists = lists,
            Log2MaxFrameNum = log2MaxFrameNum,
            PicOrderCountType = pocType,
            Log2MaxPicOrderCountLsb = log2MaxPocLsb,
            DeltaPicOrderAlwaysZero = alwaysZero,
            OffsetForNonReferencePicture = offsetNonRef,
            OffsetForTopToBottomField = offsetTopBottom,
            OffsetForReferenceFrame = offsets,
            MaxNumberOfReferenceFrames = maxRefFrames,
            GapsInFrameNumAllowed = gaps,
            WidthInMacroblocks = widthInMbs,
            HeightInMacroblocks = heightInMapUnits,
            Direct8x8Inference = direct8x8,
            CropLeft = cropLeft,
            CropRight = cropRight,
            CropTop = cropTop,
            CropBottom = cropBottom,
            Range = range,
            Matrix = matrix,
            FrameRate = rate,
            MaxNumberOfReorderFrames = reorder,
            MaxDecodedFrameBuffering = dpb
        };
    }

    private static void ReadVui(H264BitReader reader, ref VideoColorRange range, ref VideoColorMatrix? matrix, ref VideoFrameRate? rate, ref int? reorder, ref int? dpb)
    {
        if (reader.ReadFlag() && reader.ReadBits(8) == 255)
        {
            reader.SkipBits(32);
        }

        if (reader.ReadFlag())
        {
            reader.SkipBits(1);
        }

        if (reader.ReadFlag())
        {
            reader.SkipBits(3);
            range = reader.ReadFlag() ? VideoColorRange.Full : VideoColorRange.Limited;
            if (reader.ReadFlag())
            {
                reader.SkipBits(16);
                matrix = reader.ReadBits(8) switch
                {
                    1 => VideoColorMatrix.Bt709,
                    5 or 6 => VideoColorMatrix.Bt601,
                    _ => null
                };
            }
        }

        if (reader.ReadFlag())
        {
            reader.ReadUe();
            reader.ReadUe();
        }

        if (reader.ReadFlag())
        {
            var unitsInTick = reader.ReadBits(32);
            var timeScale = reader.ReadBits(32);
            reader.SkipBits(1);
            if (unitsInTick > 0 && timeScale > 0 && timeScale <= int.MaxValue && 2L * unitsInTick <= int.MaxValue)
            {
                var numerator = (int)timeScale;
                var denominator = (int)(2 * unitsInTick);
                var divisor = (int)GreatestCommonDivisor((uint)numerator, (uint)denominator);
                rate = new VideoFrameRate(numerator / divisor, denominator / divisor);
            }
        }

        var nalHrd = reader.ReadFlag();
        if (nalHrd)
        {
            SkipHrd(reader);
        }

        var vclHrd = reader.ReadFlag();
        if (vclHrd)
        {
            SkipHrd(reader);
        }

        if (nalHrd || vclHrd)
        {
            reader.SkipBits(1);
        }

        reader.SkipBits(1);
        if (reader.ReadFlag())
        {
            reader.SkipBits(1);
            reader.ReadUe();
            reader.ReadUe();
            reader.ReadUe();
            reader.ReadUe();
            reorder = (int)reader.ReadUe(16, "max_num_reorder_frames");
            dpb = (int)reader.ReadUe(16, "max_dec_frame_buffering");
        }
    }

    private static void SkipHrd(H264BitReader reader)
    {
        var count = reader.ReadUe(31, "cpb_cnt_minus1") + 1;
        reader.SkipBits(8);
        for (var i = 0; i < count; i++)
        {
            reader.ReadUe();
            reader.ReadUe();
            reader.SkipBits(1);
        }

        reader.SkipBits(20);
    }

    private static uint GreatestCommonDivisor(uint a, uint b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }

    private static int MaxDpbMacroblocks(int level) => level switch
    {
        9 or 10 => 396,
        11 => 900,
        12 or 13 or 20 => 2376,
        21 => 4752,
        22 or 30 => 8100,
        31 => 18000,
        32 => 20480,
        40 or 41 => 32768,
        42 => 34816,
        50 => 110400,
        51 or 52 => 184320,
        _ => 696320
    };
}

internal sealed class PictureParameterSet
{
    public int Id { get; private init; }

    public int SequenceParameterSetId { get; private init; }

    public bool EntropyCodingCabac { get; private init; }

    public bool BottomFieldPicOrderInFramePresent { get; private init; }

    public int DefaultReferenceCountL0 { get; private init; }

    public int DefaultReferenceCountL1 { get; private init; }

    public bool WeightedPrediction { get; private init; }

    public int WeightedBipredictionIdc { get; private init; }

    public int InitialQp { get; private init; }

    public int ChromaQpIndexOffset { get; private init; }

    public int SecondChromaQpIndexOffset { get; private init; }

    public bool DeblockingFilterControlPresent { get; private init; }

    public bool ConstrainedIntraPrediction { get; private init; }

    public bool RedundantPictureCountPresent { get; private init; }

    public bool Transform8x8Mode { get; private init; }

    public byte[]?[]? ExplicitScalingLists { get; private init; }

    public static PictureParameterSet Parse(H264BitReader reader)
    {
        var id = (int)reader.ReadUe(255, "pic_parameter_set_id");
        var spsId = (int)reader.ReadUe(31, "seq_parameter_set_id");
        var cabac = reader.ReadFlag();
        var bottomField = reader.ReadFlag();
        if (reader.ReadUe(7, "num_slice_groups_minus1") != 0)
        {
            throw new NotSupportedException("H.264: slice groups (FMO) are not supported");
        }

        var l0 = (int)reader.ReadUe(31, "num_ref_idx_l0_default_active_minus1") + 1;
        var l1 = (int)reader.ReadUe(31, "num_ref_idx_l1_default_active_minus1") + 1;
        var weighted = reader.ReadFlag();
        var bipred = (int)reader.ReadBits(2);
        if (bipred == 3)
        {
            throw new InvalidDataException("H.264: weighted_bipred_idc 3 is reserved");
        }

        var qp = 26 + reader.ReadSe(-26, 25, "pic_init_qp_minus26");
        reader.ReadSe(-26, 25, "pic_init_qs_minus26");
        var chromaOffset = reader.ReadSe(-12, 12, "chroma_qp_index_offset");
        var deblockingControl = reader.ReadFlag();
        var constrained = reader.ReadFlag();
        var redundant = reader.ReadFlag();
        var transform8x8 = false;
        byte[]?[]? explicitLists = null;
        var secondChromaOffset = chromaOffset;
        if (reader.MoreRbspData())
        {
            transform8x8 = reader.ReadFlag();
            if (reader.ReadFlag())
            {
                explicitLists = H264.ScalingLists.ReadExplicit(reader, 6 + (transform8x8 ? 2 : 0));
            }

            secondChromaOffset = reader.ReadSe(-12, 12, "second_chroma_qp_index_offset");
        }

        return new PictureParameterSet
        {
            Id = id,
            SequenceParameterSetId = spsId,
            EntropyCodingCabac = cabac,
            BottomFieldPicOrderInFramePresent = bottomField,
            DefaultReferenceCountL0 = l0,
            DefaultReferenceCountL1 = l1,
            WeightedPrediction = weighted,
            WeightedBipredictionIdc = bipred,
            InitialQp = qp,
            ChromaQpIndexOffset = chromaOffset,
            SecondChromaQpIndexOffset = secondChromaOffset,
            DeblockingFilterControlPresent = deblockingControl,
            ConstrainedIntraPrediction = constrained,
            RedundantPictureCountPresent = redundant,
            Transform8x8Mode = transform8x8,
            ExplicitScalingLists = explicitLists
        };
    }

    public byte[][] ScalingListsFor(SequenceParameterSet sps) =>
        ExplicitScalingLists is { } lists
            ? H264.ScalingLists.Resolve(lists, sps.HasScalingMatrix ? sps.ScalingLists : null)
            : sps.ScalingLists;
}
