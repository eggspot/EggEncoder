using EggEncoder.Transform;

namespace EggEncoder.Codecs.H264;

internal sealed partial class MacroblockEncoder
{
    private const int Intra16x16ModeBits = 4;
    private const int Intra4x4MacroblockBits = 6;

    private readonly byte[] _intra4x4Totals = new byte[16];
    private readonly int[] _dequantized = new int[16];

    private static bool Intra4x4ModeAvailable(int mode, bool left, bool top, bool topLeft) => mode switch
    {
        0 or 3 or 7 => top,
        1 or 8 => left,
        2 => true,
        _ => top && left && topLeft
    };

    private int AnalyseIntra16x16(out int bestMode)
    {
        var (left, top, topLeft) = _slice.IntraMacroblockNeighbours();
        bestMode = 2;
        var bestCost = int.MaxValue;
        for (var mode = 0; mode < 4; mode++)
        {
            if ((mode == 0 && !top) || (mode == 1 && !left) || (mode == 3 && !(left && top && topLeft)))
            {
                continue;
            }

            IntraPrediction.Predict16x16(_recon, _lumaOffset, _strideY, mode, left, top, topLeft);
            var cost = BlockCost.Satd(SourceY, _source.StrideY, _recon.AsSpan(_lumaOffset), _strideY, 16, 16) + (_lambda * Intra16x16ModeBits);
            if (cost < bestCost)
            {
                bestCost = cost;
                bestMode = mode;
            }
        }

        return bestCost;
    }

    private int AnalyseIntra4x4()
    {
        var modes = _picture.IntraModes.AsSpan(_slice.Macroblock * 16, 16);
        var scratch = _intra4x4;
        scratch.ClearResidual();
        Array.Clear(_intra4x4Totals);
        var source = SourceY;
        var total = _lambda * Intra4x4MacroblockBits;
        for (var block = 0; block < 16; block++)
        {
            var bx = MacroblockLayout.BlockX[block];
            var by = MacroblockLayout.BlockY[block];
            var (left, top, topLeft, topRight) = _slice.IntraBlockNeighbours(bx, by, block);
            var predicted = _slice.PredictedIntra4x4Mode(bx, by);
            var offset = _lumaOffset + (by * 4 * _strideY) + (bx * 4);
            var blockSource = source[((by * 4 * _source.StrideY) + (bx * 4))..];
            var bestMode = 2;
            var bestCost = int.MaxValue;
            for (var mode = 0; mode < 9; mode++)
            {
                if (!Intra4x4ModeAvailable(mode, left, top, topLeft))
                {
                    continue;
                }

                IntraPrediction.Predict4x4(_recon, offset, _strideY, mode, left, top, topLeft, topRight);
                var cost = BlockCost.Satd(blockSource, _source.StrideY, _recon.AsSpan(offset), _strideY, 4, 4) + (_lambda * (mode == predicted ? 1 : 4));
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestMode = mode;
                }
            }

            IntraPrediction.Predict4x4(_recon, offset, _strideY, bestMode, left, top, topLeft, topRight);
            ResidualBlock(blockSource, _source.StrideY, _recon.AsSpan(offset), _strideY);
            ForwardTransform.Core4x4(_residual.AsSpan(0, 16), _coefficients);
            var count = Quantizer.Quantize4x4(_coefficients, _raster, _scanned, _qp, intra: true, 0);
            if (count > 0)
            {
                _raster.CopyTo(scratch.LumaCoefficients, block * 16);
                scratch.LumaBlockCoded[block] = true;
                _intra4x4Totals[(by * 4) + bx] = (byte)count;
                _dequantizer.Dequantize4x4(_raster, _dequantized, _qp, 0, keepDc: false);
                InverseTransform.Add4x4(_dequantized, _recon, offset, _strideY);
            }

            modes[(by * 4) + bx] = (byte)bestMode;
            _intra4x4Modes[block] = (byte)bestMode;
            scratch.PreviousIntraModeFlags[block] = bestMode == predicted;
            scratch.RemainingIntraModes[block] = (byte)(bestMode < predicted ? bestMode : bestMode - 1);
            total += bestCost;
        }

        return total;
    }

    private void UseIntra4x4()
    {
        var syntax = _slice.Syntax;
        syntax.ClearResidual();
        syntax.Kind = MacroblockKind.IntraNxN;
        syntax.Transform8x8 = false;
        syntax.QpDelta = 0;
        syntax.PartitionCount = 0;
        var scratch = _intra4x4;
        scratch.PreviousIntraModeFlags.CopyTo(syntax.PreviousIntraModeFlags, 0);
        scratch.RemainingIntraModes.CopyTo(syntax.RemainingIntraModes, 0);
        scratch.LumaCoefficients.CopyTo(syntax.LumaCoefficients, 0);
        scratch.LumaBlockCoded.CopyTo(syntax.LumaBlockCoded, 0);
        var totals = _picture.TotalCoefficientsLuma.AsSpan(_slice.Macroblock * 16, 16);
        _intra4x4Totals.CopyTo(totals);
        var pattern = 0;
        for (var block = 0; block < 16; block++)
        {
            if (syntax.LumaBlockCoded[block])
            {
                pattern |= 1 << (block >> 2);
            }
        }

        syntax.CodedBlockPatternLuma = pattern;
    }

    private void EncodeIntra16x16(int mode)
    {
        var (left, top, topLeft) = _slice.IntraMacroblockNeighbours();
        IntraPrediction.Predict16x16(_recon, _lumaOffset, _strideY, mode, left, top, topLeft);
        for (var y = 0; y < 16; y++)
        {
            _recon.AsSpan(_lumaOffset + (y * _strideY), 16).CopyTo(_lumaPrediction.AsSpan(y * 16, 16));
        }

        var syntax = _slice.Syntax;
        syntax.ClearResidual();
        syntax.Kind = MacroblockKind.Intra16x16;
        syntax.Intra16x16PredictionMode = mode;
        syntax.Transform8x8 = false;
        syntax.QpDelta = 0;
        syntax.PartitionCount = 0;
        var totals = _picture.TotalCoefficientsLuma.AsSpan(_slice.Macroblock * 16, 16);
        Span<int> dc = stackalloc int[16];
        var source = SourceY;
        var anyAc = false;
        for (var block = 0; block < 16; block++)
        {
            var bx = MacroblockLayout.BlockX[block] * 4;
            var by = MacroblockLayout.BlockY[block] * 4;
            ResidualBlock(source[((by * _source.StrideY) + bx)..], _source.StrideY, _lumaPrediction.AsSpan((by * 16) + bx), 16);
            ForwardTransform.Core4x4(_residual.AsSpan(0, 16), _coefficients);
            dc[((by / 4) * 4) + (bx / 4)] = _coefficients[0];
            var count = Quantizer.Quantize4x4(_coefficients, _raster, _scanned, _qp, intra: true, 1);
            if (count > 0)
            {
                _raster.CopyTo(syntax.LumaCoefficients, block * 16);
                syntax.LumaBlockCoded[block] = true;
                totals[((by / 4) * 4) + (bx / 4)] = (byte)count;
                anyAc = true;
            }
        }

        ForwardTransform.Hadamard4x4(dc);
        Span<short> levels = stackalloc short[16];
        syntax.LumaDcCoded = Quantizer.QuantizeDc(dc, levels, _qp, intra: true) > 0;
        for (var scan = 0; scan < 16; scan++)
        {
            syntax.LumaDc[scan] = levels[MacroblockLayout.ZigZag4x4[scan]];
        }

        syntax.CodedBlockPatternLuma = anyAc ? 15 : 0;
    }

    private void EncodeIntraChroma()
    {
        var (left, top, topLeft) = _slice.IntraMacroblockNeighbours();
        var bestMode = 0;
        var bestCost = int.MaxValue;
        for (var mode = 0; mode < 4; mode++)
        {
            if ((mode == 1 && !left) || (mode == 2 && !top) || (mode == 3 && !(left && top && topLeft)))
            {
                continue;
            }

            IntraPrediction.PredictChroma(_recon, _offsetU, _strideUV, mode, left, top, topLeft);
            IntraPrediction.PredictChroma(_recon, _offsetV, _strideUV, mode, left, top, topLeft);
            var cost = BlockCost.Satd(SourceU, _source.StrideUV, _recon.AsSpan(_offsetU), _strideUV, 8, 8)
                + BlockCost.Satd(SourceV, _source.StrideUV, _recon.AsSpan(_offsetV), _strideUV, 8, 8);
            if (cost < bestCost)
            {
                bestCost = cost;
                bestMode = mode;
            }
        }

        IntraPrediction.PredictChroma(_recon, _offsetU, _strideUV, bestMode, left, top, topLeft);
        IntraPrediction.PredictChroma(_recon, _offsetV, _strideUV, bestMode, left, top, topLeft);
        for (var y = 0; y < 8; y++)
        {
            _recon.AsSpan(_offsetU + (y * _strideUV), 8).CopyTo(_cbPrediction.AsSpan(y * 8, 8));
            _recon.AsSpan(_offsetV + (y * _strideUV), 8).CopyTo(_crPrediction.AsSpan(y * 8, 8));
        }

        _slice.Syntax.IntraChromaPredictionMode = bestMode;
        EncodeChromaResidual(intra: true);
    }

    private void EncodeInter(int mvX, int mvY, int predictedX, int predictedY)
    {
        PredictInter(mvX, mvY);
        var syntax = _slice.Syntax;
        syntax.ClearResidual();
        syntax.Kind = MacroblockKind.P16x16;
        syntax.Shape = InterShape.Partition16x16;
        syntax.Transform8x8 = false;
        syntax.QpDelta = 0;
        syntax.PartitionCount = 1;
        syntax.Partitions[0] = new InterPartition
        {
            X = 0,
            Y = 0,
            Width = 4,
            Height = 4,
            PartitionIndex = 0,
            ReferenceL0 = 0,
            ReferenceL1 = -1,
            UsesL0 = true,
            MvdL0X = mvX - predictedX,
            MvdL0Y = mvY - predictedY
        };
        var totals = _picture.TotalCoefficientsLuma.AsSpan(_slice.Macroblock * 16, 16);
        Span<int> scores = stackalloc int[4];
        scores.Clear();
        Span<int> counts = stackalloc int[16];
        var source = SourceY;
        for (var block = 0; block < 16; block++)
        {
            var bx = MacroblockLayout.BlockX[block] * 4;
            var by = MacroblockLayout.BlockY[block] * 4;
            ResidualBlock(source[((by * _source.StrideY) + bx)..], _source.StrideY, _lumaPrediction.AsSpan((by * 16) + bx), 16);
            ForwardTransform.Core4x4(_residual.AsSpan(0, 16), _coefficients);
            counts[block] = Quantizer.Quantize4x4(_coefficients, _raster, _scanned, _qp, intra: false, 0);
            _raster.CopyTo(syntax.LumaCoefficients, block * 16);
            scores[block >> 2] += counts[block] == 0 ? 0 : DecimationScore(_scanned);
        }

        var totalScore = scores[0] + scores[1] + scores[2] + scores[3];
        var pattern = 0;
        for (var block = 0; block < 16; block++)
        {
            var keep = counts[block] > 0 && scores[block >> 2] >= 4 && totalScore >= 6;
            if (!keep)
            {
                syntax.LumaCoefficients.AsSpan(block * 16, 16).Clear();
                continue;
            }

            syntax.LumaBlockCoded[block] = true;
            totals[(MacroblockLayout.BlockY[block] * 4) + MacroblockLayout.BlockX[block]] = (byte)counts[block];
            pattern |= 1 << (block >> 2);
        }

        syntax.CodedBlockPatternLuma = pattern;
        EncodeChromaResidual(intra: false);
    }

    private void EncodeChromaResidual(bool intra)
    {
        var syntax = _slice.Syntax;
        var totals = _picture.TotalCoefficientsChroma.AsSpan(_slice.Macroblock * 8, 8);
        Span<int> dc = stackalloc int[4];
        Span<short> levels = stackalloc short[4];
        Span<byte> acCounts = stackalloc byte[8];
        var anyAc = false;
        var anyDc = false;
        for (var plane = 0; plane < 2; plane++)
        {
            var source = plane == 0 ? SourceU : SourceV;
            var prediction = plane == 0 ? _cbPrediction : _crPrediction;
            var score = 0;
            for (var block = 0; block < 4; block++)
            {
                var bx = (block & 1) * 4;
                var by = (block >> 1) * 4;
                ResidualBlock(source[((by * _source.StrideUV) + bx)..], _source.StrideUV, prediction.AsSpan((by * 8) + bx), 8);
                ForwardTransform.Core4x4(_residual.AsSpan(0, 16), _coefficients);
                dc[block] = _coefficients[0];
                var count = Quantizer.Quantize4x4(_coefficients, _raster, _scanned, _chromaQp, intra, 1);
                _raster.CopyTo(syntax.ChromaAc, ((plane * 4) + block) * 16);
                acCounts[(plane * 4) + block] = (byte)count;
                score += count == 0 ? 0 : DecimationScore(_scanned.AsSpan(0, 15));
            }

            if (!intra && score < 7)
            {
                syntax.ChromaAc.AsSpan(plane * 64, 64).Clear();
                acCounts.Slice(plane * 4, 4).Clear();
            }

            ForwardTransform.Hadamard2x2(dc);
            var dcCount = Quantizer.QuantizeDc(dc, levels, _chromaQp, intra);
            levels.CopyTo(syntax.ChromaDc.AsSpan(plane * 4, 4));
            syntax.ChromaDcCoded[plane] = dcCount > 0;
            anyDc |= dcCount > 0;
        }

        for (var i = 0; i < 8; i++)
        {
            anyAc |= acCounts[i] > 0;
        }

        syntax.CodedBlockPatternChroma = anyAc ? 2 : anyDc ? 1 : 0;
        if (!anyAc)
        {
            return;
        }

        for (var i = 0; i < 8; i++)
        {
            if (acCounts[i] > 0)
            {
                syntax.ChromaAcBlockCoded[i] = true;
                totals[i] = acCounts[i];
            }
        }
    }

    private void WriteMacroblock(BitWriter bits, MacroblockSyntax syntax)
    {
        var inter = _reference is not null;
        switch (syntax.Kind)
        {
            case MacroblockKind.IntraNxN:
                bits.WriteUe(inter ? 5u : 0u);
                for (var block = 0; block < 16; block++)
                {
                    bits.WriteFlag(syntax.PreviousIntraModeFlags[block]);
                    if (!syntax.PreviousIntraModeFlags[block])
                    {
                        bits.WriteBits(syntax.RemainingIntraModes[block], 3);
                    }
                }

                bits.WriteUe((uint)syntax.IntraChromaPredictionMode);
                bits.WriteUe((uint)IntraCodeNumbers[syntax.CodedBlockPatternLuma | (syntax.CodedBlockPatternChroma << 4)]);
                break;
            case MacroblockKind.Intra16x16:
                var mbType = 1 + syntax.Intra16x16PredictionMode + (4 * syntax.CodedBlockPatternChroma) + (syntax.CodedBlockPatternLuma == 15 ? 12 : 0);
                bits.WriteUe((uint)(inter ? 5 + mbType : mbType));
                bits.WriteUe((uint)syntax.IntraChromaPredictionMode);
                break;
            default:
                bits.WriteUe(0);
                bits.WriteSe(syntax.Partitions[0].MvdL0X);
                bits.WriteSe(syntax.Partitions[0].MvdL0Y);
                bits.WriteUe((uint)InterCodeNumbers[syntax.CodedBlockPatternLuma | (syntax.CodedBlockPatternChroma << 4)]);
                break;
        }

        if (syntax.Kind == MacroblockKind.Intra16x16 || syntax.CodedBlockPatternLuma > 0 || syntax.CodedBlockPatternChroma > 0)
        {
            bits.WriteSe(syntax.QpDelta);
            WriteResidual(bits, syntax);
        }
    }

    private void WriteResidual(BitWriter bits, MacroblockSyntax syntax)
    {
        Span<short> scanned = stackalloc short[16];
        var ac = syntax.Kind == MacroblockKind.Intra16x16;
        if (ac)
        {
            CavlcWriter.WriteBlock(bits, _slice.LumaCoefficientPrediction(0, 0), 16, syntax.LumaDc);
        }

        for (var block = 0; block < 16; block++)
        {
            if ((syntax.CodedBlockPatternLuma & (1 << (block >> 2))) == 0)
            {
                continue;
            }

            var coefficients = syntax.LumaCoefficients.AsSpan(block * 16, 16);
            var count = ac ? 15 : 16;
            for (var i = 0; i < count; i++)
            {
                scanned[i] = coefficients[MacroblockLayout.ZigZag4x4[i + (ac ? 1 : 0)]];
            }

            CavlcWriter.WriteBlock(bits, _slice.LumaCoefficientPrediction(MacroblockLayout.BlockX[block], MacroblockLayout.BlockY[block]), count, scanned);
        }

        if (syntax.CodedBlockPatternChroma == 0)
        {
            return;
        }

        for (var plane = 0; plane < 2; plane++)
        {
            CavlcWriter.WriteBlock(bits, -1, 4, syntax.ChromaDc.AsSpan(plane * 4, 4));
        }

        if (syntax.CodedBlockPatternChroma < 2)
        {
            return;
        }

        for (var plane = 0; plane < 2; plane++)
        {
            for (var block = 0; block < 4; block++)
            {
                var coefficients = syntax.ChromaAc.AsSpan(((plane * 4) + block) * 16, 16);
                for (var i = 0; i < 15; i++)
                {
                    scanned[i] = coefficients[MacroblockLayout.ZigZag4x4[i + 1]];
                }

                CavlcWriter.WriteBlock(bits, _slice.ChromaCoefficientPrediction(plane, block & 1, block >> 1), 15, scanned);
            }
        }
    }
}
