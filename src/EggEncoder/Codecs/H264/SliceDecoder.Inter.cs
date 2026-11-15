namespace EggEncoder.Codecs.H264;

internal sealed partial class SliceDecoder
{
    private void StartInterMacroblockP(int mbType)
    {
        var syntax = _syntax;
        syntax.ClearResidual();
        syntax.Transform8x8 = false;
        syntax.QpDelta = 0;
        syntax.PartitionCount = 0;
        switch (mbType)
        {
            case 0:
                syntax.Kind = MacroblockKind.P16x16;
                syntax.Shape = InterShape.Partition16x16;
                AddPartition(0, 0, 4, 4, 0);
                break;
            case 1:
                syntax.Kind = MacroblockKind.P16x8;
                syntax.Shape = InterShape.Partition16x8;
                AddPartition(0, 0, 4, 2, 0);
                AddPartition(0, 2, 4, 2, 1);
                break;
            case 2:
                syntax.Kind = MacroblockKind.P8x16;
                syntax.Shape = InterShape.Partition8x16;
                AddPartition(0, 0, 2, 4, 0);
                AddPartition(2, 0, 2, 4, 1);
                break;
            default:
                syntax.Kind = MacroblockKind.P8x8;
                syntax.Shape = InterShape.Partition8x8;
                break;
        }
    }

    private void ParseInterMacroblockP(int mbType)
    {
        var syntax = _syntax;
        var referenceCount = _header.ReferenceCountL0;
        StartInterMacroblockP(mbType);
        if (syntax.Shape == InterShape.Partition8x8)
        {
            ParseSubMacroblocksP(mbType == 4, referenceCount);
        }
        else
        {
            var parts = syntax.PartitionCount;
            for (var i = 0; i < parts; i++)
            {
                syntax.Partitions[i].ReferenceL0 = referenceCount > 1 ? ReadReferenceIndex(referenceCount) : 0;
                syntax.Partitions[i].UsesL0 = true;
            }

            for (var i = 0; i < parts; i++)
            {
                syntax.Partitions[i].MvdL0X = _reader.ReadSe(-8192, 8191, "mvd_l0");
                syntax.Partitions[i].MvdL0Y = _reader.ReadSe(-8192, 8191, "mvd_l0");
            }
        }

        var pattern = MacroblockLayout.InterCodedBlockPattern[_reader.ReadUe(47, "coded_block_pattern")];
        syntax.CodedBlockPatternLuma = pattern & 15;
        syntax.CodedBlockPatternChroma = pattern >> 4;
        if (syntax.CodedBlockPatternLuma > 0 && _pps.Transform8x8Mode && NoPartitionSmallerThan8x8())
        {
            syntax.Transform8x8 = _reader.ReadFlag();
        }

        if (syntax.CodedBlockPatternLuma > 0 || syntax.CodedBlockPatternChroma > 0)
        {
            syntax.QpDelta = _reader.ReadSe(-26, 25, "mb_qp_delta");
            ReadResidual();
        }
    }

    private void ParseSubMacroblocksP(bool referenceZero, int referenceCount)
    {
        var syntax = _syntax;
        for (var i = 0; i < 4; i++)
        {
            syntax.SubMacroblockTypes[i] = (int)_reader.ReadUe(3, "sub_mb_type");
        }

        Span<int> references = stackalloc int[4];
        for (var i = 0; i < 4; i++)
        {
            references[i] = !referenceZero && referenceCount > 1 ? ReadReferenceIndex(referenceCount) : 0;
        }

        for (var i = 0; i < 4; i++)
        {
            var first = syntax.PartitionCount;
            AddSubPartitions(i, syntax.SubMacroblockTypes[i]);
            for (var p = first; p < syntax.PartitionCount; p++)
            {
                syntax.Partitions[p].ReferenceL0 = references[i];
                syntax.Partitions[p].UsesL0 = true;
                syntax.Partitions[p].MvdL0X = _reader.ReadSe(-8192, 8191, "mvd_l0");
                syntax.Partitions[p].MvdL0Y = _reader.ReadSe(-8192, 8191, "mvd_l0");
            }
        }
    }

    private void AddSubPartitions(int index, int shape)
    {
        var x = (index & 1) * 2;
        var y = (index >> 1) * 2;
        switch (shape)
        {
            case 0:
                AddPartition(x, y, 2, 2, index);
                break;
            case 1:
                AddPartition(x, y, 2, 1, index);
                AddPartition(x, y + 1, 2, 1, index);
                break;
            case 2:
                AddPartition(x, y, 1, 2, index);
                AddPartition(x + 1, y, 1, 2, index);
                break;
            default:
                AddPartition(x, y, 1, 1, index);
                AddPartition(x + 1, y, 1, 1, index);
                AddPartition(x, y + 1, 1, 1, index);
                AddPartition(x + 1, y + 1, 1, 1, index);
                break;
        }
    }

    private void AddPartition(int x, int y, int width, int height, int partitionIndex)
    {
        _syntax.Partitions[_syntax.PartitionCount++] = new InterPartition
        {
            X = x,
            Y = y,
            Width = width,
            Height = height,
            PartitionIndex = partitionIndex,
            ReferenceL0 = -1,
            ReferenceL1 = -1
        };
    }

    private int ReadReferenceIndex(int count)
    {
        var value = (int)_reader.ReadTe((uint)(count - 1));
        if (value >= count)
        {
            throw new InvalidDataException("H.264: a reference index is outside the reference list");
        }

        return value;
    }

    private void DecodeSkippedMacroblock()
    {
        var syntax = _syntax;
        syntax.ClearResidual();
        syntax.CodedBlockPatternLuma = 0;
        syntax.CodedBlockPatternChroma = 0;
        syntax.QpDelta = 0;
        syntax.Transform8x8 = false;
        syntax.Kind = MacroblockKind.PSkip;
        syntax.PartitionCount = 0;
        AddPartition(0, 0, 4, 4, 0);
        syntax.Partitions[0].ReferenceL0 = 0;
        syntax.Partitions[0].UsesL0 = true;
        syntax.Shape = InterShape.Partition16x16;
        ReconstructMacroblock();
    }

    private void ReconstructInter(byte[] buffer, int lumaOffset, int lumaStride, int offsetU, int offsetV, int chromaStride)
    {
        var syntax = _syntax;
        _spatialDirectReady = false;
        for (var p = 0; p < syntax.PartitionCount; p++)
        {
            ref var partition = ref syntax.Partitions[p];
            if (partition.Direct)
            {
                DeriveDirectMotion(ref partition);
            }
            else if (syntax.Kind == MacroblockKind.PSkip)
            {
                var (mvX, mvY) = PredictSkipMotion();
                AssignMotion(ref partition, 0, 0, mvX, mvY);
            }
            else
            {
                if (partition.UsesL0)
                {
                    var (mvX, mvY) = PredictMotion(ref partition, 0);
                    AssignMotion(ref partition, 0, partition.ReferenceL0, mvX + partition.MvdL0X, mvY + partition.MvdL0Y);
                }

                if (partition.UsesL1)
                {
                    var (mvX, mvY) = PredictMotion(ref partition, 1);
                    AssignMotion(ref partition, 1, partition.ReferenceL1, mvX + partition.MvdL1X, mvY + partition.MvdL1Y);
                }
            }

            for (var y = partition.Y; y < partition.Y + partition.Height; y++)
            {
                for (var x = partition.X; x < partition.X + partition.Width; x++)
                {
                    _motionAssigned[(y * 4) + x] = true;
                }
            }
        }

        if (syntax.Kind is MacroblockKind.BSkip or MacroblockKind.BDirect16x16 && HasUniformDirectMotion())
        {
            var whole = syntax.Partitions[0];
            whole.Width = 4;
            whole.Height = 4;
            PredictPartition(ref whole, buffer, lumaOffset, lumaStride, offsetU, offsetV, chromaStride);
        }
        else
        {
            for (var p = 0; p < syntax.PartitionCount; p++)
            {
                PredictPartition(ref syntax.Partitions[p], buffer, lumaOffset, lumaStride, offsetU, offsetV, chromaStride);
            }
        }

        if (syntax.Transform8x8)
        {
            AddLumaResidual8x8(buffer, lumaOffset, lumaStride, 1);
        }
        else if (syntax.CodedBlockPatternLuma != 0)
        {
            for (var block = 0; block < 16; block++)
            {
                if (!syntax.LumaBlockCoded[block])
                {
                    continue;
                }

                var offset = lumaOffset + (MacroblockLayout.BlockY[block] * 4 * lumaStride) + (MacroblockLayout.BlockX[block] * 4);
                var d = _dequantized.AsSpan(0, 16);
                _dequantizer.Dequantize4x4(syntax.LumaCoefficients.AsSpan(block * 16, 16), d, _qp, 3, keepDc: false);
                InverseTransform.Add4x4(d, buffer, offset, lumaStride);
            }
        }

        AddChromaResidual(buffer, offsetU, offsetV, chromaStride, intra: false);
    }

    private void AssignMotion(ref InterPartition partition, int list, int reference, int mvX, int mvY)
    {
        if (mvX is < -32768 or > 32767 || mvY is < -32768 or > 32767)
        {
            throw new InvalidDataException("H.264: a motion vector is out of range");
        }

        var references = list == 0 ? _referencesL0 : _referencesL1;
        if ((uint)reference >= (uint)references.Length || references[reference] is null)
        {
            throw new InvalidDataException("H.264: a macroblock refers to a missing reference picture");
        }

        var vectors = _picture.MotionVectors[list];
        for (var y = partition.Y; y < partition.Y + partition.Height; y++)
        {
            for (var x = partition.X; x < partition.X + partition.Width; x++)
            {
                var index = (_macroblock * 16) + (y * 4) + x;
                vectors[2 * index] = (short)mvX;
                vectors[(2 * index) + 1] = (short)mvY;
            }
        }

        for (var y8 = partition.Y >> 1; y8 <= (partition.Y + partition.Height - 1) >> 1; y8++)
        {
            for (var x8 = partition.X >> 1; x8 <= (partition.X + partition.Width - 1) >> 1; x8++)
            {
                var slot = (_macroblock * 4) + (y8 * 2) + x8;
                _picture.ReferenceIndexes[list][slot] = (sbyte)reference;
                _picture.ReferencePictureIds[list][slot] = references[reference].PictureId;
            }
        }
    }

    private (int X, int Y) PredictSkipMotion()
    {
        var a = NeighbourMotion(-1, 0, 0);
        var b = NeighbourMotion(0, -1, 0);
        if (!a.Available || !b.Available || (a.Reference == 0 && a.X == 0 && a.Y == 0) || (b.Reference == 0 && b.X == 0 && b.Y == 0))
        {
            return (0, 0);
        }

        var partition = _syntax.Partitions[0];
        return PredictMotion(ref partition, 0);
    }

    private (int X, int Y) PredictMotion(ref InterPartition partition, int list)
    {
        var reference = list == 0 ? partition.ReferenceL0 : partition.ReferenceL1;
        var a = NeighbourMotion(partition.X - 1, partition.Y, list);
        var b = NeighbourMotion(partition.X, partition.Y - 1, list);
        var c = NeighbourMotion(partition.X + partition.Width, partition.Y - 1, list);
        if (!c.Available)
        {
            c = NeighbourMotion(partition.X - 1, partition.Y - 1, list);
        }

        if (!b.Available && !c.Available && a.Available)
        {
            b = a;
            c = a;
        }

        switch (_syntax.Shape)
        {
            case InterShape.Partition16x8 when partition.PartitionIndex == 0 && b.Reference == reference:
                return (b.X, b.Y);
            case InterShape.Partition16x8 when partition.PartitionIndex == 1 && a.Reference == reference:
                return (a.X, a.Y);
            case InterShape.Partition8x16 when partition.PartitionIndex == 0 && a.Reference == reference:
                return (a.X, a.Y);
            case InterShape.Partition8x16 when partition.PartitionIndex == 1 && c.Reference == reference:
                return (c.X, c.Y);
        }

        var matches = (a.Reference == reference ? 1 : 0) + (b.Reference == reference ? 1 : 0) + (c.Reference == reference ? 1 : 0);
        if (matches == 1)
        {
            var only = a.Reference == reference ? a : b.Reference == reference ? b : c;
            return (only.X, only.Y);
        }

        return (Median(a.X, b.X, c.X), Median(a.Y, b.Y, c.Y));
    }

    private static int Median(int a, int b, int c) => Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));

    private (bool Available, int Reference, int X, int Y) NeighbourMotion(int bx, int by, int list)
    {
        if (bx >= 0 && by >= 0 && bx < 4 && by < 4)
        {
            if (!_motionAssigned[(by * 4) + bx])
            {
                return (false, -1, 0, 0);
            }

            return MotionAt(_macroblock, bx, by, list);
        }

        if (bx >= 4 && by >= 0)
        {
            return (false, -1, 0, 0);
        }

        var (macroblock, x, y) = Locate(bx, by, 4);
        if (macroblock < 0)
        {
            return (false, -1, 0, 0);
        }

        if (_picture.IsIntra(macroblock))
        {
            return (true, -1, 0, 0);
        }

        return MotionAt(macroblock, x, y, list);
    }

    private (bool Available, int Reference, int X, int Y) MotionAt(int macroblock, int x, int y, int list)
    {
        var reference = _picture.ReferenceIndexes[list][(macroblock * 4) + ((y >> 1) * 2) + (x >> 1)];
        if (reference < 0)
        {
            return (true, -1, 0, 0);
        }

        var index = (macroblock * 16) + (y * 4) + x;
        var vectors = _picture.MotionVectors[list];
        return (true, reference, vectors[2 * index], vectors[(2 * index) + 1]);
    }

    private void PredictPartition(ref InterPartition partition, byte[] buffer, int lumaOffset, int lumaStride, int offsetU, int offsetV, int chromaStride)
    {
        var width = partition.Width * 4;
        var height = partition.Height * 4;
        var index = (_macroblock * 16) + (partition.Y * 4) + partition.X;
        var x = partition.X * 4;
        var y = partition.Y * 4;
        var luma = buffer.AsSpan(lumaOffset + (y * lumaStride) + x);
        var cb = buffer.AsSpan(offsetU + ((y / 2) * chromaStride) + (x / 2));
        var cr = buffer.AsSpan(offsetV + ((y / 2) * chromaStride) + (x / 2));
        if (partition.UsesL0 && partition.UsesL1)
        {
            Span<byte> luma1 = stackalloc byte[width * height];
            Span<byte> cb1 = stackalloc byte[width * height / 4];
            Span<byte> cr1 = stackalloc byte[width * height / 4];
            PredictFromList(0, partition.ReferenceL0, index, partition, luma, lumaStride, cb, cr, chromaStride);
            PredictFromList(1, partition.ReferenceL1, index, partition, luma1, width, cb1, cr1, width / 2);
            CombineBipredicted(partition.ReferenceL0, partition.ReferenceL1, luma, lumaStride, cb, cr, chromaStride, luma1, cb1, cr1, width, height);
        }
        else
        {
            var reference = partition.UsesL0 ? partition.ReferenceL0 : partition.ReferenceL1;
            var list = partition.UsesL0 ? 0 : 1;
            PredictFromList(list, reference, index, partition, luma, lumaStride, cb, cr, chromaStride);
            ApplyWeights(list, reference, luma, lumaStride, cb, cr, chromaStride, width, height);
        }
    }

    private void PredictFromList(int list, int reference, int index, InterPartition partition, Span<byte> luma, int lumaStride, Span<byte> cb, Span<byte> cr, int chromaStride)
    {
        var picture = (list == 0 ? _referencesL0 : _referencesL1)[reference];
        var vectors = _picture.MotionVectors[list];
        int mvX = vectors[2 * index];
        int mvY = vectors[(2 * index) + 1];
        var width = partition.Width * 4;
        var height = partition.Height * 4;
        var x = (_mbX * 16) + (partition.X * 4);
        var y = (_mbY * 16) + (partition.Y * 4);
        InterPrediction.Luma(picture, x + (mvX >> 2), y + (mvY >> 2), mvX & 3, mvY & 3, width, height, luma, lumaStride);
        InterPrediction.Chroma(picture, false, (x / 2) + (mvX >> 3), (y / 2) + (mvY >> 3), mvX & 7, mvY & 7, width / 2, height / 2, cb, chromaStride);
        InterPrediction.Chroma(picture, true, (x / 2) + (mvX >> 3), (y / 2) + (mvY >> 3), mvX & 7, mvY & 7, width / 2, height / 2, cr, chromaStride);
    }

    private void ApplyWeights(int list, int reference, Span<byte> luma, int lumaStride, Span<byte> cb, Span<byte> cr, int chromaStride, int width, int height)
    {
        if (_header.Weights is not { } weights)
        {
            return;
        }

        var (lumaWeight, lumaOffset) = weights.Luma[list][reference];
        InterPrediction.WeightExplicit(luma, lumaStride, width, height, weights.LumaLog2Denominator, lumaWeight, lumaOffset);
        var chroma = weights.Chroma[list][reference];
        InterPrediction.WeightExplicit(cb, chromaStride, width / 2, height / 2, weights.ChromaLog2Denominator, chroma[0].Weight, chroma[0].Offset);
        InterPrediction.WeightExplicit(cr, chromaStride, width / 2, height / 2, weights.ChromaLog2Denominator, chroma[1].Weight, chroma[1].Offset);
    }
}
