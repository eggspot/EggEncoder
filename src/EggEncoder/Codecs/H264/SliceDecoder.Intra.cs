namespace EggEncoder.Codecs.H264;

internal sealed partial class SliceDecoder
{
    private void ReconstructMacroblock()
    {
        var syntax = _syntax;
        var picture = _picture;
        if (syntax.Kind != MacroblockKind.IntraPcm)
        {
            _qp = (_qp + syntax.QpDelta + 52) % 52;
        }

        picture.Kinds[_macroblock] = syntax.Kind;
        picture.SliceNumbers[_macroblock] = _sliceNumber;
        picture.QpY[_macroblock] = (sbyte)_qp;
        picture.Transform8x8[_macroblock] = syntax.Transform8x8;

        var frame = picture.Frame;
        var buffer = frame.Buffer;
        var lumaOffset = frame.OffsetY + (_mbY * 16 * frame.StrideY) + (_mbX * 16);
        var chromaOffsetU = frame.OffsetU + (_mbY * 8 * frame.StrideUV) + (_mbX * 8);
        var chromaOffsetV = frame.OffsetV + (_mbY * 8 * frame.StrideUV) + (_mbX * 8);
        switch (syntax.Kind)
        {
            case MacroblockKind.IntraPcm:
                ReconstructPcm(buffer, lumaOffset, frame.StrideY, chromaOffsetU, chromaOffsetV, frame.StrideUV);
                return;
            case MacroblockKind.IntraNxN:
                ReconstructIntra4x4(buffer, lumaOffset, frame.StrideY);
                break;
            case MacroblockKind.Intra16x16:
                ReconstructIntra16x16(buffer, lumaOffset, frame.StrideY);
                break;
            default:
                ReconstructInter(buffer, lumaOffset, frame.StrideY, chromaOffsetU, chromaOffsetV, frame.StrideUV);
                return;
        }

        ReconstructIntraChroma(buffer, chromaOffsetU, chromaOffsetV, frame.StrideUV);
    }

    private void ReconstructPcm(byte[] buffer, int lumaOffset, int lumaStride, int offsetU, int offsetV, int chromaStride)
    {
        var samples = _syntax.PcmSamples;
        for (var y = 0; y < 16; y++)
        {
            samples.AsSpan(y * 16, 16).CopyTo(buffer.AsSpan(lumaOffset + (y * lumaStride), 16));
        }

        for (var y = 0; y < 8; y++)
        {
            samples.AsSpan(256 + (y * 8), 8).CopyTo(buffer.AsSpan(offsetU + (y * chromaStride), 8));
            samples.AsSpan(320 + (y * 8), 8).CopyTo(buffer.AsSpan(offsetV + (y * chromaStride), 8));
        }

        _picture.TotalCoefficientsLuma.AsSpan(_macroblock * 16, 16).Fill(16);
        _picture.TotalCoefficientsChroma.AsSpan(_macroblock * 8, 8).Fill(16);
        _picture.IntraModes.AsSpan(_macroblock * 16, 16).Fill(2);
    }

    private void ReconstructIntra4x4(byte[] buffer, int lumaOffset, int stride)
    {
        var syntax = _syntax;
        var modes = _picture.IntraModes.AsSpan(_macroblock * 16, 16);
        for (var block = 0; block < 16; block++)
        {
            var bx = MacroblockLayout.BlockX[block];
            var by = MacroblockLayout.BlockY[block];
            var predicted = PredictedIntraMode(bx, by, modes);
            var mode = syntax.PreviousIntraModeFlags[block]
                ? predicted
                : syntax.RemainingIntraModes[block] < predicted ? syntax.RemainingIntraModes[block] : syntax.RemainingIntraModes[block] + 1;
            modes[(by * 4) + bx] = (byte)mode;

            var offset = lumaOffset + (by * 4 * stride) + (bx * 4);
            IntraPrediction.Predict4x4(
                buffer,
                offset,
                stride,
                mode,
                BlockSamplesAvailable(bx - 1, by, block),
                BlockSamplesAvailable(bx, by - 1, block),
                BlockSamplesAvailable(bx - 1, by - 1, block),
                BlockSamplesAvailable(bx + 1, by - 1, block));
            if (syntax.LumaBlockCoded[block])
            {
                var d = _dequantized.AsSpan(0, 16);
                _dequantizer.Dequantize4x4(syntax.LumaCoefficients.AsSpan(block * 16, 16), d, _qp, 0, keepDc: false);
                InverseTransform.Add4x4(d, buffer, offset, stride);
            }
        }
    }

    private int PredictedIntraMode(int bx, int by, Span<byte> currentModes)
    {
        var (macroblockA, xA, yA) = Locate(bx - 1, by, 4);
        var (macroblockB, xB, yB) = Locate(bx, by - 1, 4);
        if (!IsAvailableForIntra(macroblockA) || !IsAvailableForIntra(macroblockB))
        {
            return 2;
        }

        var modeA = NeighbourIntraMode(macroblockA, xA, yA, currentModes);
        var modeB = NeighbourIntraMode(macroblockB, xB, yB, currentModes);
        return Math.Min(modeA, modeB);
    }

    private int NeighbourIntraMode(int macroblock, int x, int y, Span<byte> currentModes)
    {
        if (macroblock == _macroblock)
        {
            return currentModes[(y * 4) + x];
        }

        return _picture.Kinds[macroblock] == MacroblockKind.IntraNxN ? _picture.IntraModes[(macroblock * 16) + (y * 4) + x] : 2;
    }

    private bool BlockSamplesAvailable(int bx, int by, int currentBlock)
    {
        if (bx >= 0 && by >= 0 && bx < 4 && by < 4)
        {
            return MacroblockLayout.RasterToBlock[(by * 4) + bx] < currentBlock;
        }

        if (bx >= 4 && by >= 0)
        {
            return false;
        }

        var (macroblock, _, _) = Locate(bx, by, 4);
        return IsAvailableForIntra(macroblock);
    }

    private void ReconstructIntra16x16(byte[] buffer, int lumaOffset, int stride)
    {
        var syntax = _syntax;
        _picture.IntraModes.AsSpan(_macroblock * 16, 16).Fill(2);
        IntraPrediction.Predict16x16(
            buffer,
            lumaOffset,
            stride,
            syntax.Intra16x16PredictionMode,
            IsAvailableForIntra(NeighbourMacroblock(-1, 0)),
            IsAvailableForIntra(NeighbourMacroblock(0, -1)),
            IsAvailableForIntra(NeighbourMacroblock(-1, -1)));

        Span<int> dc = stackalloc int[16];
        dc.Clear();
        if (syntax.LumaDcCoded)
        {
            _dequantizer.LumaDc(syntax.LumaDc, dc, _qp, 0);
        }

        for (var block = 0; block < 16; block++)
        {
            var bx = MacroblockLayout.BlockX[block];
            var by = MacroblockLayout.BlockY[block];
            var offset = lumaOffset + (by * 4 * stride) + (bx * 4);
            var blockDc = dc[(by * 4) + bx];
            if (syntax.LumaBlockCoded[block])
            {
                var d = _dequantized.AsSpan(0, 16);
                _dequantizer.Dequantize4x4(syntax.LumaCoefficients.AsSpan(block * 16, 16), d, _qp, 0, keepDc: true);
                d[0] = blockDc;
                InverseTransform.Add4x4(d, buffer, offset, stride);
            }
            else if (blockDc != 0)
            {
                InverseTransform.AddDcOnly(blockDc, buffer, offset, stride, 4);
            }
        }
    }

    private void ReconstructIntraChroma(byte[] buffer, int offsetU, int offsetV, int stride)
    {
        var left = IsAvailableForIntra(NeighbourMacroblock(-1, 0));
        var top = IsAvailableForIntra(NeighbourMacroblock(0, -1));
        var topLeft = IsAvailableForIntra(NeighbourMacroblock(-1, -1));
        IntraPrediction.PredictChroma(buffer, offsetU, stride, _syntax.IntraChromaPredictionMode, left, top, topLeft);
        IntraPrediction.PredictChroma(buffer, offsetV, stride, _syntax.IntraChromaPredictionMode, left, top, topLeft);
        AddChromaResidual(buffer, offsetU, offsetV, stride, intra: true);
    }

    private void AddChromaResidual(byte[] buffer, int offsetU, int offsetV, int stride, bool intra)
    {
        var syntax = _syntax;
        if (syntax.CodedBlockPatternChroma == 0)
        {
            return;
        }

        Span<int> dc = stackalloc int[4];
        for (var plane = 0; plane < 2; plane++)
        {
            var qp = Dequantizer.ChromaQp(_qp, plane == 0 ? _pps.ChromaQpIndexOffset : _pps.SecondChromaQpIndexOffset);
            var list = (intra ? 1 : 4) + plane;
            dc.Clear();
            if (syntax.ChromaDcCoded[plane])
            {
                _dequantizer.ChromaDc(syntax.ChromaDc.AsSpan(plane * 4, 4), dc, qp, list);
            }

            var planeOffset = plane == 0 ? offsetU : offsetV;
            for (var block = 0; block < 4; block++)
            {
                var offset = planeOffset + ((block >> 1) * 4 * stride) + ((block & 1) * 4);
                if (syntax.ChromaAcBlockCoded[(plane * 4) + block])
                {
                    var d = _dequantized.AsSpan(0, 16);
                    _dequantizer.Dequantize4x4(syntax.ChromaAc.AsSpan(((plane * 4) + block) * 16, 16), d, qp, list, keepDc: true);
                    d[0] = dc[block];
                    InverseTransform.Add4x4(d, buffer, offset, stride);
                }
                else if (dc[block] != 0)
                {
                    InverseTransform.AddDcOnly(dc[block], buffer, offset, stride, 4);
                }
            }
        }
    }
}
