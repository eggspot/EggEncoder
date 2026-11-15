namespace EggEncoder.Codecs.H264;

internal sealed partial class SliceDecoder
{
    private const int MbSkipPOffset = 11;
    private const int MbSkipBOffset = 24;
    private const int MbTypeBOffset = 27;
    private const int MbTypeBIntraOffset = 32;
    private const int SubMbTypeBOffset = 36;
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
    private const int Significant8x8Offset = 402;
    private const int LastSignificant8x8Offset = 417;
    private const int CoefficientAbsLevel8x8Offset = 426;

    private static readonly byte[] Significant8x8Increments =
    [
        0, 1, 2, 3, 4, 5, 5, 4, 4, 3, 3, 4, 4, 4, 5, 5, 4, 4, 4, 4, 3, 3, 6, 7, 7, 7, 8, 9, 10, 9, 8, 7,
        7, 6, 11, 12, 13, 11, 6, 7, 8, 9, 14, 10, 9, 8, 6, 11, 12, 13, 11, 6, 9, 14, 10, 9, 11, 12, 13, 11, 14, 10, 12
    ];

    private static readonly byte[] LastSignificant8x8Increments =
    [
        0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        3, 3, 3, 3, 3, 3, 3, 3, 4, 4, 4, 4, 4, 4, 4, 4, 5, 5, 5, 5, 6, 6, 6, 6, 7, 7, 7, 7, 8, 8, 8
    ];

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
        while (true)
        {
            BeginMacroblock();
            var skipOffset = _header.Type == SliceType.B ? MbSkipBOffset : MbSkipPOffset;
            if (_header.Type != SliceType.I && cabac.DecodeDecision(skipOffset + SkipContextIncrement()) == 1)
            {
                _cabacState!.Record(_macroblock, 0, 0, 0);
                _cabacState.ClearMotion(_macroblock);
                _previousQpDeltaNonZero = false;
                if (_header.Type == SliceType.B)
                {
                    _cabacState.DirectBlocks[_macroblock] = 0xF;
                    DecodeSkippedMacroblockB();
                }
                else
                {
                    _cabacState.DirectBlocks[_macroblock] = 0;
                    DecodeSkippedMacroblock();
                }
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
        state.DirectBlocks[_macroblock] = 0;
        if (_header.Type == SliceType.I)
        {
            ParseIntraMacroblockCabac(cabac, DecodeIntraMacroblockType(cabac, MbTypeIOffset));
            return;
        }

        if (_header.Type == SliceType.B)
        {
            var bType = DecodeMacroblockTypeB(cabac);
            if (bType < 0)
            {
                ParseIntraMacroblockCabac(cabac, DecodeIntraMacroblockType(cabac, MbTypeBIntraOffset));
            }
            else
            {
                ParseInterMacroblockBCabac(cabac, bType);
            }

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

    private int DecodeMacroblockTypeB(CabacDecoder cabac)
    {
        var a = NeighbourMacroblock(-1, 0);
        var b = NeighbourMacroblock(0, -1);
        var increment = (a >= 0 && _picture.Kinds[a] is not (MacroblockKind.BSkip or MacroblockKind.BDirect16x16) ? 1 : 0)
            + (b >= 0 && _picture.Kinds[b] is not (MacroblockKind.BSkip or MacroblockKind.BDirect16x16) ? 1 : 0);
        if (cabac.DecodeDecision(MbTypeBOffset + increment) == 0)
        {
            return 0;
        }

        if (cabac.DecodeDecision(MbTypeBOffset + 3) == 0)
        {
            return 1 + cabac.DecodeDecision(MbTypeBOffset + 5);
        }

        var b2 = cabac.DecodeDecision(MbTypeBOffset + 4);
        var b3 = cabac.DecodeDecision(MbTypeBOffset + 5);
        var b4 = cabac.DecodeDecision(MbTypeBOffset + 5);
        var b5 = cabac.DecodeDecision(MbTypeBOffset + 5);
        if (b2 == 0)
        {
            return 3 + ((b3 << 2) | (b4 << 1) | b5);
        }

        if (b3 == 0)
        {
            var b6 = cabac.DecodeDecision(MbTypeBOffset + 5);
            return 12 + ((b4 << 2) | (b5 << 1) | b6);
        }

        return (b4, b5) switch
        {
            (1, 0) => 11,
            (1, 1) => 22,
            (0, 1) => -1,
            _ => 20 + cabac.DecodeDecision(MbTypeBOffset + 5)
        };
    }

    private int DecodeSubMacroblockTypeB(CabacDecoder cabac)
    {
        if (cabac.DecodeDecision(SubMbTypeBOffset) == 0)
        {
            return 0;
        }

        if (cabac.DecodeDecision(SubMbTypeBOffset + 1) == 0)
        {
            return 1 + cabac.DecodeDecision(SubMbTypeBOffset + 3);
        }

        if (cabac.DecodeDecision(SubMbTypeBOffset + 2) == 0)
        {
            var low0 = cabac.DecodeDecision(SubMbTypeBOffset + 3);
            var low1 = cabac.DecodeDecision(SubMbTypeBOffset + 3);
            return 3 + ((low0 << 1) | low1);
        }

        if (cabac.DecodeDecision(SubMbTypeBOffset + 3) == 1)
        {
            return 11 + cabac.DecodeDecision(SubMbTypeBOffset + 3);
        }

        var high = cabac.DecodeDecision(SubMbTypeBOffset + 3);
        var low = cabac.DecodeDecision(SubMbTypeBOffset + 3);
        return 7 + ((high << 1) | low);
    }

    private void ParseInterMacroblockBCabac(CabacDecoder cabac, int mbType)
    {
        var syntax = _syntax;
        var state = _cabacState!;
        StartInterMacroblockB(mbType);
        _cabacReferences.AsSpan().Fill(-1);
        var counts = (Span<int>)[_header.ReferenceCountL0, _header.ReferenceCountL1];
        if (syntax.Kind == MacroblockKind.BDirect16x16)
        {
            state.DirectBlocks[_macroblock] = 0xF;
        }
        else if (syntax.Kind == MacroblockKind.B8x8)
        {
            for (var i = 0; i < 4; i++)
            {
                syntax.SubMacroblockTypes[i] = DecodeSubMacroblockTypeB(cabac);
                if (syntax.SubMacroblockTypes[i] == 0)
                {
                    state.DirectBlocks[_macroblock] |= (byte)(1 << i);
                }
            }

            for (var list = 0; list < 2; list++)
            {
                for (var i = 0; i < 4; i++)
                {
                    var type = syntax.SubMacroblockTypes[i];
                    if (type != 0 && (BSubModes[type] & (list == 0 ? PredictL0 : PredictL1)) != 0)
                    {
                        _cabacReferences[(list * 4) + i] = (sbyte)(counts[list] > 1 ? DecodeReferenceIndex(cabac, list, (i & 1) * 2, (i >> 1) * 2, counts[list]) : 0);
                    }
                }
            }

            for (var i = 0; i < 4; i++)
            {
                AddSubMacroblockB(i);
                for (var p = _subPartitionStarts[i]; p < _subPartitionStarts[i + 1]; p++)
                {
                    if (!syntax.Partitions[p].Direct)
                    {
                        syntax.Partitions[p].ReferenceL0 = _cabacReferences[i];
                        syntax.Partitions[p].ReferenceL1 = _cabacReferences[4 + i];
                    }
                }
            }

            for (var list = 0; list < 2; list++)
            {
                for (var p = 0; p < syntax.PartitionCount; p++)
                {
                    ref var partition = ref syntax.Partitions[p];
                    if (!partition.Direct && (list == 0 ? partition.UsesL0 : partition.UsesL1))
                    {
                        DecodeMvd(cabac, ref partition, list);
                    }
                }
            }
        }
        else
        {
            for (var list = 0; list < 2; list++)
            {
                for (var p = 0; p < syntax.PartitionCount; p++)
                {
                    ref var partition = ref syntax.Partitions[p];
                    if (!(list == 0 ? partition.UsesL0 : partition.UsesL1))
                    {
                        continue;
                    }

                    var reference = counts[list] > 1 ? DecodeReferenceIndex(cabac, list, partition.X, partition.Y, counts[list]) : 0;
                    if (list == 0)
                    {
                        partition.ReferenceL0 = reference;
                    }
                    else
                    {
                        partition.ReferenceL1 = reference;
                    }

                    for (var y8 = partition.Y >> 1; y8 <= (partition.Y + partition.Height - 1) >> 1; y8++)
                    {
                        for (var x8 = partition.X >> 1; x8 <= (partition.X + partition.Width - 1) >> 1; x8++)
                        {
                            _cabacReferences[(list * 4) + (y8 * 2) + x8] = (sbyte)reference;
                        }
                    }
                }
            }

            for (var list = 0; list < 2; list++)
            {
                for (var p = 0; p < syntax.PartitionCount; p++)
                {
                    ref var partition = ref syntax.Partitions[p];
                    if (list == 0 ? partition.UsesL0 : partition.UsesL1)
                    {
                        DecodeMvd(cabac, ref partition, list);
                    }
                }
            }
        }

        FinishInterMacroblockCabac(cabac);
    }

    private int DecodeIntraMacroblockType(CabacDecoder cabac, int offset)
    {
        int first;
        if (offset == MbTypeIOffset)
        {
            first = cabac.DecodeDecision(offset + IntraMacroblockTypeIncrement());
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
            syntax.Transform8x8 = _pps.Transform8x8Mode && DecodeTransformSize8x8(cabac) == 1;
            for (var block = 0; block < (syntax.Transform8x8 ? 4 : 16); block++)
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
                AddSubPartitions(i, syntax.SubMacroblockTypes[i]);
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

        FinishInterMacroblockCabac(cabac);
    }

    private void FinishInterMacroblockCabac(CabacDecoder cabac)
    {
        var syntax = _syntax;
        DecodeCodedBlockPattern(cabac);
        if (syntax.CodedBlockPatternLuma > 0 && _pps.Transform8x8Mode && NoPartitionSmallerThan8x8())
        {
            syntax.Transform8x8 = DecodeTransformSize8x8(cabac) == 1;
        }

        FinishMacroblockCabac(cabac);
    }

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

    private int DecodeTransformSize8x8(CabacDecoder cabac) => cabac.DecodeDecision(TransformSize8x8Offset + TransformSize8x8Increment());

    private int TransformSize8x8Increment()
    {
        var a = NeighbourMacroblock(-1, 0);
        var b = NeighbourMacroblock(0, -1);
        return (a >= 0 && _picture.Transform8x8[a] ? 1 : 0) + (b >= 0 && _picture.Transform8x8[b] ? 1 : 0);
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

        var quadrant = ((ny >> 1) * 2) + (nx >> 1);
        if ((_cabacState!.DirectBlocks[macroblock] & (1 << quadrant)) != 0)
        {
            return 0;
        }

        return _picture.ReferenceIndexes[list][(macroblock * 4) + quadrant] > 0 ? 1 : 0;
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

        StoreAbsoluteMvd(partition, list, mvdX, mvdY);
    }

    private void StoreAbsoluteMvd(in InterPartition partition, int list, int mvdX, int mvdY)
    {
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

    private int IntraChromaModeIncrement() => ChromaModeCondition(NeighbourMacroblock(-1, 0)) + ChromaModeCondition(NeighbourMacroblock(0, -1));

    private int DecodeIntraChromaMode(CabacDecoder cabac)
    {
        var increment = IntraChromaModeIncrement();
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
        var luma = 0;
        for (var b8 = 0; b8 < 4; b8++)
        {
            luma |= cabac.DecodeDecision(CodedBlockPatternLumaOffset + CodedBlockPatternLumaIncrement(b8, luma)) << b8;
        }

        var (first, second) = CodedBlockPatternChromaIncrements();
        var chroma = 0;
        if (cabac.DecodeDecision(CodedBlockPatternChromaOffset + first) == 1)
        {
            chroma = 1 + cabac.DecodeDecision(CodedBlockPatternChromaOffset + 4 + second);
        }

        _syntax.CodedBlockPatternLuma = luma;
        _syntax.CodedBlockPatternChroma = chroma;
    }

    private int IntraMacroblockTypeIncrement()
    {
        var a = NeighbourMacroblock(-1, 0);
        var b = NeighbourMacroblock(0, -1);
        return (a >= 0 && _picture.Kinds[a] != MacroblockKind.IntraNxN ? 1 : 0) + (b >= 0 && _picture.Kinds[b] != MacroblockKind.IntraNxN ? 1 : 0);
    }

    private int CodedBlockPatternLumaIncrement(int b8, int lumaSoFar)
    {
        var state = _cabacState!;
        int conditionA;
        if ((b8 & 1) == 1)
        {
            conditionA = ((lumaSoFar >> (b8 - 1)) & 1) == 0 ? 1 : 0;
        }
        else
        {
            var left = NeighbourMacroblock(-1, 0);
            conditionA = left >= 0 && ((state.CodedBlockPatterns[left] >> (b8 + 1)) & 1) == 0 ? 1 : 0;
        }

        int conditionB;
        if (b8 >= 2)
        {
            conditionB = ((lumaSoFar >> (b8 - 2)) & 1) == 0 ? 1 : 0;
        }
        else
        {
            var top = NeighbourMacroblock(0, -1);
            conditionB = top >= 0 && ((state.CodedBlockPatterns[top] >> (b8 + 2)) & 1) == 0 ? 1 : 0;
        }

        return conditionA + (2 * conditionB);
    }

    private (int First, int Second) CodedBlockPatternChromaIncrements()
    {
        var state = _cabacState!;
        var left = NeighbourMacroblock(-1, 0);
        var top = NeighbourMacroblock(0, -1);
        var chromaA = left >= 0 ? state.CodedBlockPatterns[left] >> 4 : 0;
        var chromaB = top >= 0 ? state.CodedBlockPatterns[top] >> 4 : 0;
        return ((chromaA != 0 ? 1 : 0) + (chromaB != 0 ? 2 : 0), (chromaA == 2 ? 1 : 0) + (chromaB == 2 ? 2 : 0));
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

        if (syntax.Transform8x8)
        {
            ReadLumaResidual8x8Cabac(cabac, lumaTotals);
        }

        var ac = syntax.Kind == MacroblockKind.Intra16x16;
        var category = ac ? 1 : 2;
        var coefficientCount = ac ? 15 : 16;
        for (var block = 0; block < 16 && !syntax.Transform8x8; block++)
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

    private void ReadLumaResidual8x8Cabac(CabacDecoder cabac, Span<byte> lumaTotals)
    {
        Span<short> levels = stackalloc short[64];
        for (var block8 = 0; block8 < 4; block8++)
        {
            if ((_syntax.CodedBlockPatternLuma & (1 << block8)) == 0)
            {
                continue;
            }

            levels.Clear();
            var total = ReadCoefficients8x8(cabac, levels);
            var bx = (block8 & 1) * 2;
            var by = (block8 >> 1) * 2;
            foreach (var position in (ReadOnlySpan<int>)[(by * 4) + bx, (by * 4) + bx + 1, ((by + 1) * 4) + bx, ((by + 1) * 4) + bx + 1])
            {
                lumaTotals[position] = (byte)total;
                _codedBlockFlags |= 1 << position;
            }

            StoreLuma8x8(block8, levels);
        }
    }

    private static int ReadCoefficients8x8(CabacDecoder cabac, Span<short> scanned)
    {
        Span<int> positions = stackalloc int[64];
        var found = 0;
        var ended = false;
        for (var i = 0; i < 63; i++)
        {
            if (cabac.DecodeDecision(Significant8x8Offset + Significant8x8Increments[i]) == 0)
            {
                continue;
            }

            positions[found++] = i;
            if (cabac.DecodeDecision(LastSignificant8x8Offset + LastSignificant8x8Increments[i]) == 1)
            {
                ended = true;
                break;
            }
        }

        if (!ended)
        {
            positions[found++] = 63;
        }

        ReadLevels(cabac, CoefficientAbsLevel8x8Offset, 4, positions[..found], scanned);
        return found;
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

        ReadLevels(cabac, levelBase, category == 3 ? 3 : 4, positions[..found], scanned);
        return found;
    }

    private static void ReadLevels(CabacDecoder cabac, int levelBase, int maximumGreater, ReadOnlySpan<int> positions, Span<short> scanned)
    {
        var greaterThanOne = 0;
        var equalToOne = 0;
        for (var j = positions.Length - 1; j >= 0; j--)
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
    }
}
