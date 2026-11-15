namespace EggEncoder.Codecs.H264;

internal sealed partial class SliceDecoder
{
    private const int MbSkipPOffset = 11;
    private const int MbTypeIOffset = 3;
    private const int MbTypePPrefixOffset = 14;
    private const int MbTypePIntraOffset = 17;
    private const int SubMbTypePOffset = 21;
    private const int MvdXOffset = 40;
    private const int MvdYOffset = 47;
    private const int RefIdxOffset = 54;
    private const int QpDeltaOffset = 60;
    private const int IntraChromaModeOffset = 64;
    private const int PreviousIntraModeOffset = 68;
    private const int RemainingIntraModeOffset = 69;
    private const int CodedBlockPatternLumaOffset = 73;
    private const int CodedBlockPatternChromaOffset = 77;
    private const int CodedBlockFlagOffset = 85;
    private const int SignificantCoefficientOffset = 105;
    private const int LastSignificantCoefficientOffset = 166;
    private const int CoefficientAbsLevelOffset = 227;
    private const int TransformSize8x8Offset = 399;

    private static readonly int[] CodedBlockFlagOffsets = [0, 4, 8, 12, 16];
    private static readonly int[] SignificantOffsets = [0, 15, 29, 44, 47];
    private static readonly int[] AbsLevelOffsets = [0, 10, 20, 30, 39];
    private static readonly int[] MvdBinIncrements = [0, 3, 4, 5, 6, 6, 6, 6, 6];

    private readonly sbyte[] _cabacReferences = new sbyte[8];
    private bool _previousQpDeltaNonZero;
    private int _codedBlockFlags;

    private void DecodeCabac()
    {
        var cabac = _cabac!;
        _reader.ByteAlign();
        cabac.InitializeContexts(_header.Type, _header.CabacInitIdc, _qp);
        cabac.Start(_reader.Buffer, _reader.ByteLength, _reader.Position);
        _macroblock = _header.FirstMacroblock;
        _previousQpDeltaNonZero = false;
        if (_header.Type == SliceType.B)
        {
            throw new NotSupportedException("H.264: B slices are not supported yet");
        }

        while (true)
        {
            BeginMacroblock();
            if (_header.Type == SliceType.P && cabac.DecodeDecision(MbSkipPOffset + SkipContextIncrement()) == 1)
            {
                _cabacState!.Record(_macroblock, 0, 0, 0);
                _cabacState.ClearMotion(_macroblock);
                _previousQpDeltaNonZero = false;
                DecodeSkippedMacroblock();
            }
            else
            {
                ParseMacroblockCabac(cabac);
                ReconstructMacroblock();
            }

            _macroblock++;
            if (cabac.DecodeTerminate() == 1)
            {
                break;
            }
        }
    }

    private int SkipContextIncrement()
    {
        var a = NeighbourMacroblock(-1, 0);
        var b = NeighbourMacroblock(0, -1);
        return (a >= 0 && !IsSkipped(a) ? 1 : 0) + (b >= 0 && !IsSkipped(b) ? 1 : 0);
    }

    private bool IsSkipped(int macroblock) => _picture.Kinds[macroblock] is MacroblockKind.PSkip or MacroblockKind.BSkip;

    private void ParseMacroblockCabac(CabacDecoder cabac)
    {
        var state = _cabacState!;
        state.ClearMotion(_macroblock);
        if (_header.Type == SliceType.I)
        {
            ParseIntraMacroblockCabac(cabac, DecodeIntraMacroblockType(cabac, MbTypeIOffset));
            return;
        }

        if (cabac.DecodeDecision(MbTypePPrefixOffset) == 1)
        {
            ParseIntraMacroblockCabac(cabac, DecodeIntraMacroblockType(cabac, MbTypePIntraOffset));
            return;
        }

        int mbType;
        if (cabac.DecodeDecision(MbTypePPrefixOffset + 1) == 0)
        {
            mbType = cabac.DecodeDecision(MbTypePPrefixOffset + 2) == 0 ? 0 : 3;
        }
        else
        {
            mbType = cabac.DecodeDecision(MbTypePPrefixOffset + 3) == 1 ? 1 : 2;
        }

        ParseInterMacroblockPCabac(cabac, mbType);
    }

    private int DecodeIntraMacroblockType(CabacDecoder cabac, int offset)
    {
        int first;
        if (offset == MbTypeIOffset)
        {
            var a = NeighbourMacroblock(-1, 0);
            var b = NeighbourMacroblock(0, -1);
            var increment = (a >= 0 && _picture.Kinds[a] != MacroblockKind.IntraNxN ? 1 : 0) + (b >= 0 && _picture.Kinds[b] != MacroblockKind.IntraNxN ? 1 : 0);
            first = cabac.DecodeDecision(offset + increment);
        }
        else
        {
            first = cabac.DecodeDecision(offset);
        }

        if (first == 0)
        {
            return 0;
        }

        if (cabac.DecodeTerminate() == 1)
        {
            return 25;
        }

        var prefix = offset == MbTypeIOffset;
        var luma = cabac.DecodeDecision(offset + (prefix ? 3 : 1));
        var chroma = cabac.DecodeDecision(offset + (prefix ? 4 : 2));
        if (chroma == 1)
        {
            chroma += cabac.DecodeDecision(offset + (prefix ? 5 : 2));
        }

        var high = cabac.DecodeDecision(offset + (prefix ? 6 : 3));
        var low = cabac.DecodeDecision(offset + (prefix ? 7 : 3));
        return 1 + ((high << 1) | low) + (4 * chroma) + (12 * luma);
    }

    private void ParseIntraMacroblockCabac(CabacDecoder cabac, int mbType)
    {
        var syntax = _syntax;
        var state = _cabacState!;
        StartIntraMacroblock(mbType);
        if (syntax.Kind == MacroblockKind.IntraPcm)
        {
            var start = (cabac.BitPosition + 7) & ~7L;
            var byteStart = (int)(start >> 3);
            if (byteStart + 384 > _reader.ByteLength)
            {
                throw new InvalidDataException("H.264: an I_PCM macroblock runs past the end of its slice");
            }

            _reader.Buffer.AsSpan(byteStart, 384).CopyTo(syntax.PcmSamples);
            cabac.Start(_reader.Buffer, _reader.ByteLength, start + (384 * 8));
            state.Record(_macroblock, CabacNeighbourState.AllBlocksCoded, 0x2F, 0);
            _previousQpDeltaNonZero = false;
            return;
        }

        if (syntax.Kind == MacroblockKind.IntraNxN)
        {
            if (_pps.Transform8x8Mode && DecodeTransformSize8x8(cabac) == 1)
            {
                throw new NotSupportedException("H.264: the 8x8 transform (High profile) is not supported yet");
            }

            for (var block = 0; block < 16; block++)
            {
                syntax.PreviousIntraModeFlags[block] = cabac.DecodeDecision(PreviousIntraModeOffset) == 1;
                if (!syntax.PreviousIntraModeFlags[block])
                {
                    var bit0 = cabac.DecodeDecision(RemainingIntraModeOffset);
                    var bit1 = cabac.DecodeDecision(RemainingIntraModeOffset);
                    var bit2 = cabac.DecodeDecision(RemainingIntraModeOffset);
                    syntax.RemainingIntraModes[block] = (byte)(bit0 | (bit1 << 1) | (bit2 << 2));
                }
            }
        }

        syntax.IntraChromaPredictionMode = DecodeIntraChromaMode(cabac);
        if (syntax.Kind == MacroblockKind.IntraNxN)
        {
            DecodeCodedBlockPattern(cabac);
        }

        FinishMacroblockCabac(cabac);
    }

    private void ParseInterMacroblockPCabac(CabacDecoder cabac, int mbType)
    {
        var syntax = _syntax;
        var referenceCount = _header.ReferenceCountL0;
        StartInterMacroblockP(mbType);
        _cabacReferences.AsSpan().Fill(-1);
        if (syntax.Shape == InterShape.Partition8x8)
        {
            for (var i = 0; i < 4; i++)
            {
                syntax.SubMacroblockTypes[i] = DecodeSubMacroblockTypeP(cabac);
            }

            for (var i = 0; i < 4; i++)
            {
                var reference = referenceCount > 1 ? DecodeReferenceIndex(cabac, 0, (i & 1) * 2, (i >> 1) * 2, referenceCount) : 0;
                _cabacReferences[i] = (sbyte)reference;
            }

            for (var i = 0; i < 4; i++)
            {
                var first = syntax.PartitionCount;
                AddSubPartitions(i);
                for (var p = first; p < syntax.PartitionCount; p++)
                {
                    syntax.Partitions[p].ReferenceL0 = _cabacReferences[i];
                    syntax.Partitions[p].UsesL0 = true;
                    DecodeMvd(cabac, ref syntax.Partitions[p], 0);
                }
            }
        }
        else
        {
            for (var p = 0; p < syntax.PartitionCount; p++)
            {
                ref var partition = ref syntax.Partitions[p];
                partition.ReferenceL0 = referenceCount > 1 ? DecodeReferenceIndex(cabac, 0, partition.X, partition.Y, referenceCount) : 0;
                partition.UsesL0 = true;
                for (var y8 = partition.Y >> 1; y8 <= (partition.Y + partition.Height - 1) >> 1; y8++)
                {
                    for (var x8 = partition.X >> 1; x8 <= (partition.X + partition.Width - 1) >> 1; x8++)
                    {
                        _cabacReferences[(y8 * 2) + x8] = (sbyte)partition.ReferenceL0;
                    }
                }
            }

            for (var p = 0; p < syntax.PartitionCount; p++)
            {
                DecodeMvd(cabac, ref syntax.Partitions[p], 0);
            }
        }

        DecodeCodedBlockPattern(cabac);
        if (syntax.CodedBlockPatternLuma > 0 && _pps.Transform8x8Mode && UsesOnly8x8Partitions() && DecodeTransformSize8x8(cabac) == 1)
        {
            throw new NotSupportedException("H.264: the 8x8 transform (High profile) is not supported yet");
        }

        FinishMacroblockCabac(cabac);
    }

    private bool UsesOnly8x8Partitions() =>
        _syntax.Shape != InterShape.Partition8x8
        || (_syntax.SubMacroblockTypes[0] == 0 && _syntax.SubMacroblockTypes[1] == 0 && _syntax.SubMacroblockTypes[2] == 0 && _syntax.SubMacroblockTypes[3] == 0);

    private void FinishMacroblockCabac(CabacDecoder cabac)
    {
        var syntax = _syntax;
        _codedBlockFlags = 0;
        if (syntax.CodedBlockPatternLuma > 0 || syntax.CodedBlockPatternChroma > 0 || syntax.Kind == MacroblockKind.Intra16x16)
        {
            syntax.QpDelta = DecodeQpDelta(cabac);
            ReadResidualCabac(cabac);
        }
        else
        {
            _previousQpDeltaNonZero = false;
        }

        var intra = syntax.Kind is MacroblockKind.IntraNxN or MacroblockKind.Intra16x16;
        _cabacState!.Record(_macroblock, _codedBlockFlags, syntax.CodedBlockPatternLuma | (syntax.CodedBlockPatternChroma << 4), intra ? syntax.IntraChromaPredictionMode : 0);
    }

    private int DecodeTransformSize8x8(CabacDecoder cabac)
    {
        var a = NeighbourMacroblock(-1, 0);
        var b = NeighbourMacroblock(0, -1);
        var increment = (a >= 0 && _picture.Transform8x8[a] ? 1 : 0) + (b >= 0 && _picture.Transform8x8[b] ? 1 : 0);
        return cabac.DecodeDecision(TransformSize8x8Offset + increment);
    }

    private int DecodeSubMacroblockTypeP(CabacDecoder cabac)
    {
        if (cabac.DecodeDecision(SubMbTypePOffset) == 1)
        {
            return 0;
        }

        if (cabac.DecodeDecision(SubMbTypePOffset + 1) == 0)
        {
            return 1;
        }

        return cabac.DecodeDecision(SubMbTypePOffset + 2) == 1 ? 2 : 3;
    }

    private int DecodeReferenceIndex(CabacDecoder cabac, int list, int x, int y, int count)
    {
        var increment = ReferenceGreaterThanZero(list, x - 1, y) + (2 * ReferenceGreaterThanZero(list, x, y - 1));
        if (cabac.DecodeDecision(RefIdxOffset + increment) == 0)
        {
            return 0;
        }

        if (cabac.DecodeDecision(RefIdxOffset + 4) == 0)
        {
            return CheckReference(1, count);
        }

        var value = 2;
        while (cabac.DecodeDecision(RefIdxOffset + 5) == 1)
        {
            value++;
            if (value >= 32)
            {
                throw new InvalidDataException("H.264: a reference index is outside the reference list");
            }
        }

        return CheckReference(value, count);
    }

    private static int CheckReference(int value, int count) =>
        value < count ? value : throw new InvalidDataException("H.264: a reference index is outside the reference list");

    private int ReferenceGreaterThanZero(int list, int x, int y)
    {
        if (x >= 0 && y >= 0)
        {
            return _cabacReferences[(list * 4) + ((y >> 1) * 2) + (x >> 1)] > 0 ? 1 : 0;
        }

        var (macroblock, nx, ny) = Locate(x, y, 4);
        if (macroblock < 0 || _picture.IsIntra(macroblock) || IsSkipped(macroblock))
        {
            return 0;
        }

        return _picture.ReferenceIndexes[list][(macroblock * 4) + ((ny >> 1) * 2) + (nx >> 1)] > 0 ? 1 : 0;
    }

    private void DecodeMvd(CabacDecoder cabac, ref InterPartition partition, int list)
    {
        var mvdX = DecodeMvdComponent(cabac, MvdXOffset, AbsoluteMvdSum(list, partition.X, partition.Y, 0));
        var mvdY = DecodeMvdComponent(cabac, MvdYOffset, AbsoluteMvdSum(list, partition.X, partition.Y, 1));
        if (list == 0)
        {
            partition.MvdL0X = mvdX;
            partition.MvdL0Y = mvdY;
        }
        else
        {
            partition.MvdL1X = mvdX;
            partition.MvdL1Y = mvdY;
        }

        var absX = (byte)Math.Min(Math.Abs(mvdX), 255);
        var absY = (byte)Math.Min(Math.Abs(mvdY), 255);
        var stored = _cabacState!.AbsoluteMvd[list];
        for (var y = partition.Y; y < partition.Y + partition.Height; y++)
        {
            for (var x = partition.X; x < partition.X + partition.Width; x++)
            {
                var index = ((_macroblock * 16) + (y * 4) + x) * 2;
                stored[index] = absX;
                stored[index + 1] = absY;
            }
        }
    }

    private int AbsoluteMvdSum(int list, int x, int y, int component) =>
        NeighbourAbsoluteMvd(list, x - 1, y, component) + NeighbourAbsoluteMvd(list, x, y - 1, component);

    private int NeighbourAbsoluteMvd(int list, int x, int y, int component)
    {
        var (macroblock, nx, ny) = Locate(x, y, 4);
        if (macroblock < 0)
        {
            return 0;
        }

        return _cabacState!.AbsoluteMvd[list][(((macroblock * 16) + (ny * 4) + nx) * 2) + component];
    }

    private static int DecodeMvdComponent(CabacDecoder cabac, int offset, int sum)
    {
        var increment = sum < 3 ? 0 : sum > 32 ? 2 : 1;
        if (cabac.DecodeDecision(offset + increment) == 0)
        {
            return 0;
        }

        var value = 1;
        while (value < 9 && cabac.DecodeDecision(offset + MvdBinIncrements[value]) == 1)
        {
            value++;
        }

        if (value >= 9)
        {
            value += cabac.DecodeExpGolombBypass(3);
            if (value > 8192)
            {
                throw new InvalidDataException("H.264: a motion vector difference is out of range");
            }
        }

        return cabac.DecodeBypass() == 1 ? -value : value;
    }

    private int DecodeIntraChromaMode(CabacDecoder cabac)
    {
        var increment = ChromaModeCondition(NeighbourMacroblock(-1, 0)) + ChromaModeCondition(NeighbourMacroblock(0, -1));
        if (cabac.DecodeDecision(IntraChromaModeOffset + increment) == 0)
        {
            return 0;
        }

        if (cabac.DecodeDecision(IntraChromaModeOffset + 3) == 0)
        {
            return 1;
        }

        return cabac.DecodeDecision(IntraChromaModeOffset + 3) == 0 ? 2 : 3;
    }

    private int ChromaModeCondition(int macroblock) =>
        macroblock >= 0 && _picture.Kinds[macroblock] is MacroblockKind.IntraNxN or MacroblockKind.Intra16x16 && _cabacState!.IntraChromaModes[macroblock] != 0 ? 1 : 0;

    private void DecodeCodedBlockPattern(CabacDecoder cabac)
    {
        var state = _cabacState!;
        var left = NeighbourMacroblock(-1, 0);
        var top = NeighbourMacroblock(0, -1);
        var luma = 0;
        for (var b8 = 0; b8 < 4; b8++)
        {
            int conditionA;
            if ((b8 & 1) == 1)
            {
                conditionA = ((luma >> (b8 - 1)) & 1) == 0 ? 1 : 0;
            }
            else
            {
                conditionA = left >= 0 && ((state.CodedBlockPatterns[left] >> (b8 + 1)) & 1) == 0 ? 1 : 0;
            }

            int conditionB;
            if (b8 >= 2)
            {
                conditionB = ((luma >> (b8 - 2)) & 1) == 0 ? 1 : 0;
            }
            else
            {
                conditionB = top >= 0 && ((state.CodedBlockPatterns[top] >> (b8 + 2)) & 1) == 0 ? 1 : 0;
            }

            luma |= cabac.DecodeDecision(CodedBlockPatternLumaOffset + conditionA + (2 * conditionB)) << b8;
        }

        var chromaA = left >= 0 ? state.CodedBlockPatterns[left] >> 4 : 0;
        var chromaB = top >= 0 ? state.CodedBlockPatterns[top] >> 4 : 0;
        var chroma = 0;
        if (cabac.DecodeDecision(CodedBlockPatternChromaOffset + (chromaA != 0 ? 1 : 0) + (chromaB != 0 ? 2 : 0)) == 1)
        {
            chroma = 1 + cabac.DecodeDecision(CodedBlockPatternChromaOffset + 4 + (chromaA == 2 ? 1 : 0) + (chromaB == 2 ? 2 : 0));
        }

        _syntax.CodedBlockPatternLuma = luma;
        _syntax.CodedBlockPatternChroma = chroma;
    }

    private int DecodeQpDelta(CabacDecoder cabac)
    {
        if (cabac.DecodeDecision(QpDeltaOffset + (_previousQpDeltaNonZero ? 1 : 0)) == 0)
        {
            _previousQpDeltaNonZero = false;
            return 0;
        }

        var k = 1;
        var context = QpDeltaOffset + 2;
        while (cabac.DecodeDecision(context) == 1)
        {
            k++;
            context = QpDeltaOffset + 3;
            if (k > 52)
            {
                throw new InvalidDataException("H.264: mb_qp_delta is out of range");
            }
        }

        var value = (k & 1) == 1 ? (k + 1) >> 1 : -(k >> 1);
        if (value is < -26 or > 25)
        {
            throw new InvalidDataException("H.264: mb_qp_delta is out of range");
        }

        _previousQpDeltaNonZero = true;
        return value;
    }

    private void ReadResidualCabac(CabacDecoder cabac)
    {
        var syntax = _syntax;
        var lumaTotals = _picture.TotalCoefficientsLuma.AsSpan(_macroblock * 16, 16);
        var chromaTotals = _picture.TotalCoefficientsChroma.AsSpan(_macroblock * 8, 8);
        Span<short> scanned = stackalloc short[16];
        var intra = syntax.Kind is MacroblockKind.IntraNxN or MacroblockKind.Intra16x16;
        if (syntax.Kind == MacroblockKind.Intra16x16)
        {
            var increment = MacroblockFlagCondition(NeighbourMacroblock(-1, 0), CabacNeighbourState.LumaDcFlag, intra)
                + (2 * MacroblockFlagCondition(NeighbourMacroblock(0, -1), CabacNeighbourState.LumaDcFlag, intra));
            if (cabac.DecodeDecision(CodedBlockFlagOffset + CodedBlockFlagOffsets[0] + increment) == 1)
            {
                _codedBlockFlags |= CabacNeighbourState.LumaDcFlag;
                scanned.Clear();
                ReadCoefficients(cabac, 0, 16, scanned);
                syntax.LumaDcCoded = true;
                scanned.CopyTo(syntax.LumaDc);
            }
        }

        var ac = syntax.Kind == MacroblockKind.Intra16x16;
        var category = ac ? 1 : 2;
        var coefficientCount = ac ? 15 : 16;
        for (var block = 0; block < 16; block++)
        {
            if ((syntax.CodedBlockPatternLuma & (1 << (block >> 2))) == 0)
            {
                continue;
            }

            var bx = MacroblockLayout.BlockX[block];
            var by = MacroblockLayout.BlockY[block];
            var increment = LumaFlagCondition(bx - 1, by, intra) + (2 * LumaFlagCondition(bx, by - 1, intra));
            if (cabac.DecodeDecision(CodedBlockFlagOffset + CodedBlockFlagOffsets[category] + increment) == 0)
            {
                continue;
            }

            _codedBlockFlags |= 1 << ((by * 4) + bx);
            scanned.Clear();
            var total = ReadCoefficients(cabac, category, coefficientCount, scanned);
            lumaTotals[(by * 4) + bx] = (byte)total;
            syntax.LumaBlockCoded[block] = true;
            var coefficients = syntax.LumaCoefficients.AsSpan(block * 16, 16);
            for (var i = 0; i < coefficientCount; i++)
            {
                coefficients[MacroblockLayout.ZigZag4x4[i + (ac ? 1 : 0)]] = scanned[i];
            }
        }

        if (syntax.CodedBlockPatternChroma == 0)
        {
            return;
        }

        for (var plane = 0; plane < 2; plane++)
        {
            var flag = CabacNeighbourState.ChromaDcFlag(plane);
            var increment = MacroblockFlagCondition(NeighbourMacroblock(-1, 0), flag, intra)
                + (2 * MacroblockFlagCondition(NeighbourMacroblock(0, -1), flag, intra));
            if (cabac.DecodeDecision(CodedBlockFlagOffset + CodedBlockFlagOffsets[3] + increment) == 0)
            {
                continue;
            }

            _codedBlockFlags |= flag;
            scanned.Clear();
            ReadCoefficients(cabac, 3, 4, scanned);
            syntax.ChromaDcCoded[plane] = true;
            scanned[..4].CopyTo(syntax.ChromaDc.AsSpan(plane * 4, 4));
        }

        if (syntax.CodedBlockPatternChroma < 2)
        {
            return;
        }

        for (var plane = 0; plane < 2; plane++)
        {
            for (var block = 0; block < 4; block++)
            {
                var cx = block & 1;
                var cy = block >> 1;
                var increment = ChromaFlagCondition(plane, cx - 1, cy, intra) + (2 * ChromaFlagCondition(plane, cx, cy - 1, intra));
                if (cabac.DecodeDecision(CodedBlockFlagOffset + CodedBlockFlagOffsets[4] + increment) == 0)
                {
                    continue;
                }

                _codedBlockFlags |= CabacNeighbourState.ChromaAcFlag(plane, cx, cy);
                scanned.Clear();
                var total = ReadCoefficients(cabac, 4, 15, scanned);
                chromaTotals[(plane * 4) + block] = (byte)total;
                syntax.ChromaAcBlockCoded[(plane * 4) + block] = true;
                var coefficients = syntax.ChromaAc.AsSpan(((plane * 4) + block) * 16, 16);
                for (var i = 0; i < 15; i++)
                {
                    coefficients[MacroblockLayout.ZigZag4x4[i + 1]] = scanned[i];
                }
            }
        }
    }

    private int MacroblockFlagCondition(int macroblock, int flag, bool currentIntra)
    {
        if (macroblock < 0)
        {
            return currentIntra ? 1 : 0;
        }

        return (_cabacState!.CodedBlockFlags[macroblock] & flag) != 0 ? 1 : 0;
    }

    private int LumaFlagCondition(int x, int y, bool currentIntra)
    {
        var (macroblock, nx, ny) = Locate(x, y, 4);
        if (macroblock < 0)
        {
            return currentIntra ? 1 : 0;
        }

        var flags = macroblock == _macroblock ? _codedBlockFlags : _cabacState!.CodedBlockFlags[macroblock];
        return (flags >> ((ny * 4) + nx)) & 1;
    }

    private int ChromaFlagCondition(int plane, int x, int y, bool currentIntra)
    {
        var (macroblock, nx, ny) = Locate(x, y, 2);
        if (macroblock < 0)
        {
            return currentIntra ? 1 : 0;
        }

        var flags = macroblock == _macroblock ? _codedBlockFlags : _cabacState!.CodedBlockFlags[macroblock];
        return (flags & CabacNeighbourState.ChromaAcFlag(plane, nx, ny)) != 0 ? 1 : 0;
    }

    private static int ReadCoefficients(CabacDecoder cabac, int category, int count, Span<short> scanned)
    {
        var significantBase = SignificantCoefficientOffset + SignificantOffsets[category];
        var lastBase = LastSignificantCoefficientOffset + SignificantOffsets[category];
        var levelBase = CoefficientAbsLevelOffset + AbsLevelOffsets[category];
        Span<int> positions = stackalloc int[16];
        var found = 0;
        var ended = false;
        for (var i = 0; i < count - 1; i++)
        {
            var increment = category == 3 ? Math.Min(i, 2) : i;
            if (cabac.DecodeDecision(significantBase + increment) == 0)
            {
                continue;
            }

            positions[found++] = i;
            if (cabac.DecodeDecision(lastBase + increment) == 1)
            {
                ended = true;
                break;
            }
        }

        if (!ended)
        {
            positions[found++] = count - 1;
        }

        var greaterThanOne = 0;
        var equalToOne = 0;
        var maximumGreater = category == 3 ? 3 : 4;
        for (var j = found - 1; j >= 0; j--)
        {
            var first = greaterThanOne != 0 ? 0 : Math.Min(4, 1 + equalToOne);
            int level;
            if (cabac.DecodeDecision(levelBase + first) == 0)
            {
                level = 1;
            }
            else
            {
                var context = levelBase + 5 + Math.Min(maximumGreater, greaterThanOne);
                var prefix = 1;
                while (prefix < 14 && cabac.DecodeDecision(context) == 1)
                {
                    prefix++;
                }

                if (prefix == 14)
                {
                    prefix += cabac.DecodeExpGolombBypass(0);
                }

                level = prefix + 1;
            }

            var negative = cabac.DecodeBypass() == 1;
            if (level > 32768 || (level == 32768 && !negative))
            {
                throw new InvalidDataException("H.264: a coefficient level is out of range");
            }

            if (level == 1)
            {
                equalToOne++;
            }
            else
            {
                greaterThanOne++;
            }

            scanned[positions[j]] = (short)(negative ? -level : level);
        }

        return found;
    }
}
