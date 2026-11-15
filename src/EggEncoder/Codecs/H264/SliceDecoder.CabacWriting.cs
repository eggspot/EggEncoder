namespace EggEncoder.Codecs.H264;

internal sealed partial class SliceDecoder
{
    public void StartCabacWriting()
    {
        _previousQpDeltaNonZero = false;
    }

    public void WriteSkippedMacroblockCabac(CabacEncoder cabac)
    {
        var state = _cabacState!;
        cabac.EncodeDecision(MbSkipPOffset + SkipContextIncrement(), 1);
        state.Record(_macroblock, 0, 0, 0);
        state.ClearMotion(_macroblock);
        state.DirectBlocks[_macroblock] = 0;
        _previousQpDeltaNonZero = false;
    }

    public void WriteMacroblockCabac(CabacEncoder cabac)
    {
        var syntax = _syntax;
        var state = _cabacState!;
        if (_header.Type == SliceType.P)
        {
            cabac.EncodeDecision(MbSkipPOffset + SkipContextIncrement(), 0);
        }

        state.ClearMotion(_macroblock);
        state.DirectBlocks[_macroblock] = 0;
        var intra = syntax.Kind is MacroblockKind.IntraNxN or MacroblockKind.Intra16x16;
        if (intra)
        {
            var mbType = syntax.Kind == MacroblockKind.IntraNxN
                ? 0
                : 1 + syntax.Intra16x16PredictionMode + (4 * syntax.CodedBlockPatternChroma) + (syntax.CodedBlockPatternLuma == 15 ? 12 : 0);
            if (_header.Type == SliceType.P)
            {
                cabac.EncodeDecision(MbTypePPrefixOffset, 1);
                EncodeIntraMacroblockType(cabac, MbTypePIntraOffset, mbType);
            }
            else
            {
                EncodeIntraMacroblockType(cabac, MbTypeIOffset, mbType);
            }

            if (syntax.Kind == MacroblockKind.IntraNxN)
            {
                if (_pps.Transform8x8Mode)
                {
                    cabac.EncodeDecision(TransformSize8x8Offset + TransformSize8x8Increment(), syntax.Transform8x8 ? 1 : 0);
                }

                for (var block = 0; block < (syntax.Transform8x8 ? 4 : 16); block++)
                {
                    cabac.EncodeDecision(PreviousIntraModeOffset, syntax.PreviousIntraModeFlags[block] ? 1 : 0);
                    if (!syntax.PreviousIntraModeFlags[block])
                    {
                        int remaining = syntax.RemainingIntraModes[block];
                        cabac.EncodeDecision(RemainingIntraModeOffset, remaining & 1);
                        cabac.EncodeDecision(RemainingIntraModeOffset, (remaining >> 1) & 1);
                        cabac.EncodeDecision(RemainingIntraModeOffset, (remaining >> 2) & 1);
                    }
                }
            }

            EncodeIntraChromaMode(cabac, syntax.IntraChromaPredictionMode);
            if (syntax.Kind == MacroblockKind.IntraNxN)
            {
                EncodeCodedBlockPattern(cabac);
            }
        }
        else
        {
            cabac.EncodeDecision(MbTypePPrefixOffset, 0);
            cabac.EncodeDecision(MbTypePPrefixOffset + 1, 0);
            cabac.EncodeDecision(MbTypePPrefixOffset + 2, 0);
            ref var partition = ref syntax.Partitions[0];
            EncodeMvdComponent(cabac, MvdXOffset, AbsoluteMvdSum(0, partition.X, partition.Y, 0), partition.MvdL0X);
            EncodeMvdComponent(cabac, MvdYOffset, AbsoluteMvdSum(0, partition.X, partition.Y, 1), partition.MvdL0Y);
            StoreAbsoluteMvd(partition, 0, partition.MvdL0X, partition.MvdL0Y);
            EncodeCodedBlockPattern(cabac);
            if (syntax.CodedBlockPatternLuma > 0 && _pps.Transform8x8Mode && NoPartitionSmallerThan8x8())
            {
                cabac.EncodeDecision(TransformSize8x8Offset + TransformSize8x8Increment(), syntax.Transform8x8 ? 1 : 0);
            }
        }

        _codedBlockFlags = 0;
        if (syntax.CodedBlockPatternLuma > 0 || syntax.CodedBlockPatternChroma > 0 || syntax.Kind == MacroblockKind.Intra16x16)
        {
            EncodeQpDelta(cabac, syntax.QpDelta);
            WriteResidualCabac(cabac);
        }
        else
        {
            _previousQpDeltaNonZero = false;
        }

        state.Record(_macroblock, _codedBlockFlags, syntax.CodedBlockPatternLuma | (syntax.CodedBlockPatternChroma << 4), intra ? syntax.IntraChromaPredictionMode : 0);
    }

    private void EncodeIntraMacroblockType(CabacEncoder cabac, int offset, int mbType)
    {
        var prefix = offset == MbTypeIOffset;
        cabac.EncodeDecision(prefix ? offset + IntraMacroblockTypeIncrement() : offset, mbType == 0 ? 0 : 1);
        if (mbType == 0)
        {
            return;
        }

        cabac.EncodeTerminate(0);
        var luma = (mbType - 1) / 12;
        var chroma = (mbType - 1) / 4 % 3;
        var prediction = (mbType - 1) % 4;
        cabac.EncodeDecision(offset + (prefix ? 3 : 1), luma);
        cabac.EncodeDecision(offset + (prefix ? 4 : 2), chroma != 0 ? 1 : 0);
        if (chroma != 0)
        {
            cabac.EncodeDecision(offset + (prefix ? 5 : 2), chroma == 2 ? 1 : 0);
        }

        cabac.EncodeDecision(offset + (prefix ? 6 : 3), prediction >> 1);
        cabac.EncodeDecision(offset + (prefix ? 7 : 3), prediction & 1);
    }

    private void EncodeIntraChromaMode(CabacEncoder cabac, int mode)
    {
        cabac.EncodeDecision(IntraChromaModeOffset + IntraChromaModeIncrement(), mode == 0 ? 0 : 1);
        if (mode == 0)
        {
            return;
        }

        cabac.EncodeDecision(IntraChromaModeOffset + 3, mode == 1 ? 0 : 1);
        if (mode > 1)
        {
            cabac.EncodeDecision(IntraChromaModeOffset + 3, mode == 2 ? 0 : 1);
        }
    }

    private void EncodeCodedBlockPattern(CabacEncoder cabac)
    {
        var luma = _syntax.CodedBlockPatternLuma;
        var soFar = 0;
        for (var b8 = 0; b8 < 4; b8++)
        {
            var bin = (luma >> b8) & 1;
            cabac.EncodeDecision(CodedBlockPatternLumaOffset + CodedBlockPatternLumaIncrement(b8, soFar), bin);
            soFar |= bin << b8;
        }

        var chroma = _syntax.CodedBlockPatternChroma;
        var (first, second) = CodedBlockPatternChromaIncrements();
        cabac.EncodeDecision(CodedBlockPatternChromaOffset + first, chroma != 0 ? 1 : 0);
        if (chroma != 0)
        {
            cabac.EncodeDecision(CodedBlockPatternChromaOffset + 4 + second, chroma == 2 ? 1 : 0);
        }
    }

    private static void EncodeMvdComponent(CabacEncoder cabac, int offset, int sum, int value)
    {
        var magnitude = Math.Abs(value);
        var increment = sum < 3 ? 0 : sum > 32 ? 2 : 1;
        cabac.EncodeDecision(offset + increment, magnitude == 0 ? 0 : 1);
        if (magnitude == 0)
        {
            return;
        }

        var prefix = Math.Min(magnitude, 9);
        for (var i = 1; i < prefix; i++)
        {
            cabac.EncodeDecision(offset + MvdBinIncrements[i], 1);
        }

        if (magnitude < 9)
        {
            cabac.EncodeDecision(offset + MvdBinIncrements[magnitude], 0);
        }
        else
        {
            cabac.EncodeExpGolombBypass(magnitude - 9, 3);
        }

        cabac.EncodeBypass(value < 0 ? 1 : 0);
    }

    private void EncodeQpDelta(CabacEncoder cabac, int value)
    {
        cabac.EncodeDecision(QpDeltaOffset + (_previousQpDeltaNonZero ? 1 : 0), value == 0 ? 0 : 1);
        if (value == 0)
        {
            _previousQpDeltaNonZero = false;
            return;
        }

        var k = value > 0 ? (2 * value) - 1 : -2 * value;
        var context = QpDeltaOffset + 2;
        for (var i = 1; i < k; i++)
        {
            cabac.EncodeDecision(context, 1);
            context = QpDeltaOffset + 3;
        }

        cabac.EncodeDecision(context, 0);
        _previousQpDeltaNonZero = true;
    }

    private void WriteResidualCabac(CabacEncoder cabac)
    {
        var syntax = _syntax;
        Span<short> scanned = stackalloc short[16];
        var intra = syntax.Kind is MacroblockKind.IntraNxN or MacroblockKind.Intra16x16;
        if (syntax.Kind == MacroblockKind.Intra16x16)
        {
            var increment = MacroblockFlagCondition(NeighbourMacroblock(-1, 0), CabacNeighbourState.LumaDcFlag, intra)
                + (2 * MacroblockFlagCondition(NeighbourMacroblock(0, -1), CabacNeighbourState.LumaDcFlag, intra));
            cabac.EncodeDecision(CodedBlockFlagOffset + CodedBlockFlagOffsets[0] + increment, syntax.LumaDcCoded ? 1 : 0);
            if (syntax.LumaDcCoded)
            {
                _codedBlockFlags |= CabacNeighbourState.LumaDcFlag;
                EncodeCoefficients(cabac, 0, 16, syntax.LumaDc);
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
            var coded = syntax.LumaBlockCoded[block];
            cabac.EncodeDecision(CodedBlockFlagOffset + CodedBlockFlagOffsets[category] + increment, coded ? 1 : 0);
            if (!coded)
            {
                continue;
            }

            _codedBlockFlags |= 1 << ((by * 4) + bx);
            var coefficients = syntax.LumaCoefficients.AsSpan(block * 16, 16);
            for (var i = 0; i < coefficientCount; i++)
            {
                scanned[i] = coefficients[MacroblockLayout.ZigZag4x4[i + (ac ? 1 : 0)]];
            }

            EncodeCoefficients(cabac, category, coefficientCount, scanned);
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
            var coded = syntax.ChromaDcCoded[plane];
            cabac.EncodeDecision(CodedBlockFlagOffset + CodedBlockFlagOffsets[3] + increment, coded ? 1 : 0);
            if (coded)
            {
                _codedBlockFlags |= flag;
                EncodeCoefficients(cabac, 3, 4, syntax.ChromaDc.AsSpan(plane * 4, 4));
            }
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
                var coded = syntax.ChromaAcBlockCoded[(plane * 4) + block];
                cabac.EncodeDecision(CodedBlockFlagOffset + CodedBlockFlagOffsets[4] + increment, coded ? 1 : 0);
                if (!coded)
                {
                    continue;
                }

                _codedBlockFlags |= CabacNeighbourState.ChromaAcFlag(plane, cx, cy);
                var coefficients = syntax.ChromaAc.AsSpan(((plane * 4) + block) * 16, 16);
                for (var i = 0; i < 15; i++)
                {
                    scanned[i] = coefficients[MacroblockLayout.ZigZag4x4[i + 1]];
                }

                EncodeCoefficients(cabac, 4, 15, scanned);
            }
        }
    }

    private static void EncodeCoefficients(CabacEncoder cabac, int category, int count, ReadOnlySpan<short> scanned)
    {
        var significantBase = SignificantCoefficientOffset + SignificantOffsets[category];
        var lastBase = LastSignificantCoefficientOffset + SignificantOffsets[category];
        var levelBase = CoefficientAbsLevelOffset + AbsLevelOffsets[category];
        var last = count - 1;
        while (last > 0 && scanned[last] == 0)
        {
            last--;
        }

        for (var i = 0; i < count - 1; i++)
        {
            var increment = category == 3 ? Math.Min(i, 2) : i;
            var significant = scanned[i] != 0;
            cabac.EncodeDecision(significantBase + increment, significant ? 1 : 0);
            if (!significant)
            {
                continue;
            }

            cabac.EncodeDecision(lastBase + increment, i == last ? 1 : 0);
            if (i == last)
            {
                break;
            }
        }

        var greaterThanOne = 0;
        var equalToOne = 0;
        var maximumGreater = category == 3 ? 3 : 4;
        for (var i = last; i >= 0; i--)
        {
            if (scanned[i] == 0)
            {
                continue;
            }

            var minus1 = Math.Abs(scanned[i]) - 1;
            cabac.EncodeDecision(levelBase + (greaterThanOne != 0 ? 0 : Math.Min(4, 1 + equalToOne)), minus1 == 0 ? 0 : 1);
            if (minus1 > 0)
            {
                var context = levelBase + 5 + Math.Min(maximumGreater, greaterThanOne);
                for (var prefix = 1; prefix < Math.Min(minus1, 14); prefix++)
                {
                    cabac.EncodeDecision(context, 1);
                }

                if (minus1 < 14)
                {
                    cabac.EncodeDecision(context, 0);
                }
                else
                {
                    cabac.EncodeExpGolombBypass(minus1 - 14, 0);
                }

                greaterThanOne++;
            }
            else
            {
                equalToOne++;
            }

            cabac.EncodeBypass(scanned[i] < 0 ? 1 : 0);
        }
    }
}
