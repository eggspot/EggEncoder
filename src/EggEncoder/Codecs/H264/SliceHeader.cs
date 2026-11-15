namespace EggEncoder.Codecs.H264;

internal enum SliceType
{
    P = 0,
    B = 1,
    I = 2
}

internal readonly record struct ReferenceListModification(int Operation, int Value);

internal readonly record struct MemoryManagementOperation(int Operation, int DifferenceOfPicNumsMinus1, int LongTermPicNum, int LongTermFrameIndex, int MaxLongTermFrameIndexPlus1);

internal sealed class PredictionWeights
{
    public int LumaLog2Denominator { get; init; }

    public int ChromaLog2Denominator { get; init; }

    public required (int Weight, int Offset)[][] Luma { get; init; }

    public required (int Weight, int Offset)[][][] Chroma { get; init; }

    public required bool[][] HasLuma { get; init; }

    public required bool[][] HasChroma { get; init; }
}

internal sealed class SliceHeader
{
    public int FirstMacroblock { get; private init; }

    public SliceType Type { get; private init; }

    public int PictureParameterSetId { get; private init; }

    public int FrameNum { get; private init; }

    public bool IsIdr { get; private init; }

    public int NalReferenceIdc { get; private init; }

    public int IdrPictureId { get; private init; }

    public int PicOrderCountLsb { get; private init; }

    public int DeltaPicOrderCountBottom { get; private init; }

    public int DeltaPicOrderCount0 { get; private init; }

    public int DeltaPicOrderCount1 { get; private init; }

    public bool DirectSpatialMvPrediction { get; private init; }

    public int ReferenceCountL0 { get; private init; }

    public int ReferenceCountL1 { get; private init; }

    public ReferenceListModification[] ModificationsL0 { get; private init; } = [];

    public ReferenceListModification[] ModificationsL1 { get; private init; } = [];

    public PredictionWeights? Weights { get; private init; }

    public bool NoOutputOfPriorPictures { get; private init; }

    public bool LongTermReference { get; private init; }

    public MemoryManagementOperation[]? MemoryManagement { get; private init; }

    public int CabacInitIdc { get; private init; }

    public int QpDelta { get; private init; }

    public int DisableDeblockingFilterIdc { get; private init; }

    public int FilterOffsetA { get; private init; }

    public int FilterOffsetB { get; private init; }

    public long SliceDataPosition { get; private init; }

    public static SliceHeader Parse(
        H264BitReader reader,
        int nalUnitType,
        int nalReferenceIdc,
        IReadOnlyDictionary<int, PictureParameterSet> pictureSets,
        IReadOnlyDictionary<int, SequenceParameterSet> sequenceSets,
        out PictureParameterSet pps,
        out SequenceParameterSet sps)
    {
        var first = (int)reader.ReadUe(139263, "first_mb_in_slice");
        var rawType = (int)reader.ReadUe(9, "slice_type");
        var type = (rawType % 5) switch
        {
            0 => SliceType.P,
            1 => SliceType.B,
            2 => SliceType.I,
            _ => throw new NotSupportedException("H.264: SP and SI slices are not supported")
        };
        var ppsId = (int)reader.ReadUe(255, "pic_parameter_set_id");
        if (!pictureSets.TryGetValue(ppsId, out var foundPps))
        {
            throw new InvalidDataException($"H.264: a slice refers to picture parameter set {ppsId}, which was not sent");
        }

        if (!sequenceSets.TryGetValue(foundPps.SequenceParameterSetId, out var foundSps))
        {
            throw new InvalidDataException($"H.264: picture parameter set {ppsId} refers to sequence parameter set {foundPps.SequenceParameterSetId}, which was not sent");
        }

        pps = foundPps;
        sps = foundSps;
        if (first >= sps.MacroblockCount)
        {
            throw new InvalidDataException("H.264: first_mb_in_slice is outside the picture");
        }

        var isIdr = nalUnitType == 5;
        if (isIdr && type != SliceType.I)
        {
            throw new InvalidDataException("H.264: an IDR picture has a slice that is not intra");
        }

        var frameNum = (int)reader.ReadBits(sps.Log2MaxFrameNum);
        var idrId = isIdr ? (int)reader.ReadUe(65535, "idr_pic_id") : 0;
        int pocLsb = 0, deltaBottom = 0, delta0 = 0, delta1 = 0;
        if (sps.PicOrderCountType == 0)
        {
            pocLsb = (int)reader.ReadBits(sps.Log2MaxPicOrderCountLsb);
            if (pps.BottomFieldPicOrderInFramePresent)
            {
                deltaBottom = reader.ReadSe();
            }
        }
        else if (sps.PicOrderCountType == 1 && !sps.DeltaPicOrderAlwaysZero)
        {
            delta0 = reader.ReadSe();
            if (pps.BottomFieldPicOrderInFramePresent)
            {
                delta1 = reader.ReadSe();
            }
        }

        if (pps.RedundantPictureCountPresent && reader.ReadUe(127, "redundant_pic_cnt") != 0)
        {
            throw new NotSupportedException("H.264: redundant pictures are not supported");
        }

        var directSpatial = type == SliceType.B && reader.ReadFlag();
        var l0 = type == SliceType.I ? 0 : pps.DefaultReferenceCountL0;
        var l1 = type == SliceType.B ? pps.DefaultReferenceCountL1 : 0;
        if (type != SliceType.I && reader.ReadFlag())
        {
            l0 = (int)reader.ReadUe(31, "num_ref_idx_l0_active_minus1") + 1;
            if (type == SliceType.B)
            {
                l1 = (int)reader.ReadUe(31, "num_ref_idx_l1_active_minus1") + 1;
            }
        }

        var modificationsL0 = type == SliceType.I ? [] : ReadModifications(reader);
        var modificationsL1 = type == SliceType.B ? ReadModifications(reader) : [];
        PredictionWeights? weights = null;
        if ((pps.WeightedPrediction && type == SliceType.P) || (pps.WeightedBipredictionIdc == 1 && type == SliceType.B))
        {
            weights = ReadWeights(reader, l0, type == SliceType.B ? l1 : 0);
        }

        var noOutput = false;
        var longTerm = false;
        MemoryManagementOperation[]? operations = null;
        if (nalReferenceIdc != 0)
        {
            if (isIdr)
            {
                noOutput = reader.ReadFlag();
                longTerm = reader.ReadFlag();
            }
            else if (reader.ReadFlag())
            {
                operations = ReadMemoryManagement(reader);
            }
        }

        var cabacInit = pps.EntropyCodingCabac && type != SliceType.I ? (int)reader.ReadUe(2, "cabac_init_idc") : 0;
        var qpDelta = reader.ReadSe(-87, 77, "slice_qp_delta");
        if (pps.InitialQp + qpDelta is < 0 or > 51)
        {
            throw new InvalidDataException($"H.264: the slice QP {pps.InitialQp + qpDelta} is outside 0..51");
        }

        int disable = 0, offsetA = 0, offsetB = 0;
        if (pps.DeblockingFilterControlPresent)
        {
            disable = (int)reader.ReadUe(2, "disable_deblocking_filter_idc");
            if (disable != 1)
            {
                offsetA = 2 * reader.ReadSe(-6, 6, "slice_alpha_c0_offset_div2");
                offsetB = 2 * reader.ReadSe(-6, 6, "slice_beta_offset_div2");
            }
        }

        return new SliceHeader
        {
            FirstMacroblock = first,
            Type = type,
            PictureParameterSetId = ppsId,
            FrameNum = frameNum,
            IsIdr = isIdr,
            NalReferenceIdc = nalReferenceIdc,
            IdrPictureId = idrId,
            PicOrderCountLsb = pocLsb,
            DeltaPicOrderCountBottom = deltaBottom,
            DeltaPicOrderCount0 = delta0,
            DeltaPicOrderCount1 = delta1,
            DirectSpatialMvPrediction = directSpatial,
            ReferenceCountL0 = l0,
            ReferenceCountL1 = l1,
            ModificationsL0 = modificationsL0,
            ModificationsL1 = modificationsL1,
            Weights = weights,
            NoOutputOfPriorPictures = noOutput,
            LongTermReference = longTerm,
            MemoryManagement = operations,
            CabacInitIdc = cabacInit,
            QpDelta = qpDelta,
            DisableDeblockingFilterIdc = disable,
            FilterOffsetA = offsetA,
            FilterOffsetB = offsetB,
            SliceDataPosition = reader.Position
        };
    }

    private static ReferenceListModification[] ReadModifications(H264BitReader reader)
    {
        if (!reader.ReadFlag())
        {
            return [];
        }

        var list = new List<ReferenceListModification>();
        while (true)
        {
            var operation = (int)reader.ReadUe(5, "modification_of_pic_nums_idc");
            if (operation == 3)
            {
                return [.. list];
            }

            if (operation > 2)
            {
                throw new NotSupportedException("H.264: view reference list modifications (MVC) are not supported");
            }

            if (list.Count >= 33)
            {
                throw new InvalidDataException("H.264: too many reference list modifications");
            }

            list.Add(new ReferenceListModification(operation, (int)reader.ReadUe(131071, "abs_diff_pic_num_minus1 or long_term_pic_num")));
        }
    }

    private static PredictionWeights ReadWeights(H264BitReader reader, int countL0, int countL1)
    {
        var lumaDenominator = (int)reader.ReadUe(7, "luma_log2_weight_denom");
        var chromaDenominator = (int)reader.ReadUe(7, "chroma_log2_weight_denom");
        var luma = new (int, int)[2][];
        var chroma = new (int, int)[2][][];
        var hasLuma = new bool[2][];
        var hasChroma = new bool[2][];
        for (var list = 0; list < 2; list++)
        {
            var count = list == 0 ? countL0 : countL1;
            luma[list] = new (int, int)[count];
            chroma[list] = new (int, int)[count][];
            hasLuma[list] = new bool[count];
            hasChroma[list] = new bool[count];
            for (var i = 0; i < count; i++)
            {
                luma[list][i] = (1 << lumaDenominator, 0);
                chroma[list][i] = [(1 << chromaDenominator, 0), (1 << chromaDenominator, 0)];
                if (reader.ReadFlag())
                {
                    hasLuma[list][i] = true;
                    luma[list][i] = (reader.ReadSe(-128, 127, "luma_weight"), reader.ReadSe(-128, 127, "luma_offset"));
                }

                if (reader.ReadFlag())
                {
                    hasChroma[list][i] = true;
                    for (var c = 0; c < 2; c++)
                    {
                        chroma[list][i][c] = (reader.ReadSe(-128, 127, "chroma_weight"), reader.ReadSe(-128, 127, "chroma_offset"));
                    }
                }
            }
        }

        return new PredictionWeights
        {
            LumaLog2Denominator = lumaDenominator,
            ChromaLog2Denominator = chromaDenominator,
            Luma = luma,
            Chroma = chroma,
            HasLuma = hasLuma,
            HasChroma = hasChroma
        };
    }

    private static MemoryManagementOperation[] ReadMemoryManagement(H264BitReader reader)
    {
        var operations = new List<MemoryManagementOperation>();
        while (true)
        {
            var operation = (int)reader.ReadUe(6, "memory_management_control_operation");
            if (operation == 0)
            {
                return [.. operations];
            }

            if (operations.Count >= 66)
            {
                throw new InvalidDataException("H.264: too many memory management operations");
            }

            int difference = 0, longTermPicNum = 0, longTermIndex = 0, maxIndex = 0;
            if (operation is 1 or 3)
            {
                difference = (int)reader.ReadUe(131071, "difference_of_pic_nums_minus1");
            }

            if (operation == 2)
            {
                longTermPicNum = (int)reader.ReadUe(131071, "long_term_pic_num");
            }

            if (operation is 3 or 6)
            {
                longTermIndex = (int)reader.ReadUe(15, "long_term_frame_idx");
            }

            if (operation == 4)
            {
                maxIndex = (int)reader.ReadUe(16, "max_long_term_frame_idx_plus1");
            }

            operations.Add(new MemoryManagementOperation(operation, difference, longTermPicNum, longTermIndex, maxIndex));
        }
    }
}
