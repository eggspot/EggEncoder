namespace EggEncoder.Codecs.H264;

internal sealed partial class SliceDecoder
{
    private const int PredictL0 = 1;
    private const int PredictL1 = 2;
    private const int PredictBoth = 3;

    private static readonly byte[] BPartitionFirstModes = [0, 1, 2, 3, 1, 1, 2, 2, 1, 1, 2, 2, 1, 1, 2, 2, 3, 3, 3, 3, 3, 3];
    private static readonly byte[] BPartitionSecondModes = [0, 0, 0, 0, 1, 1, 2, 2, 2, 2, 1, 1, 3, 3, 3, 3, 1, 1, 2, 2, 3, 3];
    private static readonly byte[] BSubModes = [0, 1, 2, 3, 1, 1, 2, 2, 3, 3, 1, 2, 3];
    private static readonly byte[] BSubShapes = [0, 0, 0, 0, 1, 2, 1, 2, 1, 2, 3, 3, 3];

    private readonly int[] _subPartitionStarts = new int[5];
    private bool _spatialDirectReady;
    private int _spatialReferenceL0;
    private int _spatialReferenceL1;
    private int _spatialMvL0X;
    private int _spatialMvL0Y;
    private int _spatialMvL1X;
    private int _spatialMvL1Y;
    private bool _spatialDirectZero;

    private void StartInterMacroblockB(int mbType)
    {
        var syntax = _syntax;
        syntax.ClearResidual();
        syntax.Transform8x8 = false;
        syntax.QpDelta = 0;
        syntax.PartitionCount = 0;
        if (mbType == 0)
        {
            syntax.Kind = MacroblockKind.BDirect16x16;
            syntax.Shape = InterShape.Partition16x16;
            for (var i = 0; i < 4; i++)
            {
                AddDirectPartitions(i);
            }

            return;
        }

        if (mbType <= 3)
        {
            syntax.Kind = MacroblockKind.B16x16;
            syntax.Shape = InterShape.Partition16x16;
            AddPartition(0, 0, 4, 4, 0);
            SetPredictionMode(ref syntax.Partitions[0], BPartitionFirstModes[mbType]);
            return;
        }

        if (mbType <= 21)
        {
            if (mbType % 2 == 0)
            {
                syntax.Kind = MacroblockKind.B16x8;
                syntax.Shape = InterShape.Partition16x8;
                AddPartition(0, 0, 4, 2, 0);
                AddPartition(0, 2, 4, 2, 1);
            }
            else
            {
                syntax.Kind = MacroblockKind.B8x16;
                syntax.Shape = InterShape.Partition8x16;
                AddPartition(0, 0, 2, 4, 0);
                AddPartition(2, 0, 2, 4, 1);
            }

            SetPredictionMode(ref syntax.Partitions[0], BPartitionFirstModes[mbType]);
            SetPredictionMode(ref syntax.Partitions[1], BPartitionSecondModes[mbType]);
            return;
        }

        syntax.Kind = MacroblockKind.B8x8;
        syntax.Shape = InterShape.Partition8x8;
    }

    private void StartSkippedMacroblockB()
    {
        StartInterMacroblockB(0);
        _syntax.Kind = MacroblockKind.BSkip;
        _syntax.CodedBlockPatternLuma = 0;
        _syntax.CodedBlockPatternChroma = 0;
    }

    private static void SetPredictionMode(ref InterPartition partition, int mode)
    {
        partition.UsesL0 = (mode & PredictL0) != 0;
        partition.UsesL1 = (mode & PredictL1) != 0;
        partition.ReferenceL0 = partition.UsesL0 ? 0 : -1;
        partition.ReferenceL1 = partition.UsesL1 ? 0 : -1;
    }

    private void AddDirectPartitions(int index)
    {
        var x = (index & 1) * 2;
        var y = (index >> 1) * 2;
        var first = _syntax.PartitionCount;
        if (_sps.Direct8x8Inference)
        {
            AddPartition(x, y, 2, 2, index);
        }
        else
        {
            AddPartition(x, y, 1, 1, index);
            AddPartition(x + 1, y, 1, 1, index);
            AddPartition(x, y + 1, 1, 1, index);
            AddPartition(x + 1, y + 1, 1, 1, index);
        }

        for (var p = first; p < _syntax.PartitionCount; p++)
        {
            _syntax.Partitions[p].Direct = true;
        }
    }

    private void AddSubMacroblockB(int index)
    {
        var type = _syntax.SubMacroblockTypes[index];
        _subPartitionStarts[index] = _syntax.PartitionCount;
        if (type == 0)
        {
            AddDirectPartitions(index);
        }
        else
        {
            AddSubPartitions(index, BSubShapes[type]);
            for (var p = _subPartitionStarts[index]; p < _syntax.PartitionCount; p++)
            {
                SetPredictionMode(ref _syntax.Partitions[p], BSubModes[type]);
            }
        }

        _subPartitionStarts[index + 1] = _syntax.PartitionCount;
    }

    private bool NoPartitionSmallerThan8x8()
    {
        var syntax = _syntax;
        if (syntax.Kind is MacroblockKind.BDirect16x16 or MacroblockKind.BSkip)
        {
            return _sps.Direct8x8Inference;
        }

        if (syntax.Shape != InterShape.Partition8x8)
        {
            return true;
        }

        for (var i = 0; i < 4; i++)
        {
            var type = syntax.SubMacroblockTypes[i];
            if (_header.Type == SliceType.B)
            {
                if (type == 0 ? !_sps.Direct8x8Inference : BSubShapes[type] != 0)
                {
                    return false;
                }
            }
            else if (type != 0)
            {
                return false;
            }
        }

        return true;
    }

    private void ParseInterMacroblockB(int mbType)
    {
        var syntax = _syntax;
        StartInterMacroblockB(mbType);
        var countL0 = _header.ReferenceCountL0;
        var countL1 = _header.ReferenceCountL1;
        if (syntax.Kind == MacroblockKind.B8x8)
        {
            for (var i = 0; i < 4; i++)
            {
                syntax.SubMacroblockTypes[i] = (int)_reader.ReadUe(12, "sub_mb_type");
            }

            Span<int> referencesL0 = stackalloc int[4];
            Span<int> referencesL1 = stackalloc int[4];
            for (var i = 0; i < 4; i++)
            {
                var type = syntax.SubMacroblockTypes[i];
                referencesL0[i] = type != 0 && (BSubModes[type] & PredictL0) != 0 ? (countL0 > 1 ? ReadReferenceIndex(countL0) : 0) : -1;
            }

            for (var i = 0; i < 4; i++)
            {
                var type = syntax.SubMacroblockTypes[i];
                referencesL1[i] = type != 0 && (BSubModes[type] & PredictL1) != 0 ? (countL1 > 1 ? ReadReferenceIndex(countL1) : 0) : -1;
            }

            for (var i = 0; i < 4; i++)
            {
                AddSubMacroblockB(i);
                for (var p = _subPartitionStarts[i]; p < _subPartitionStarts[i + 1]; p++)
                {
                    if (!syntax.Partitions[p].Direct)
                    {
                        syntax.Partitions[p].ReferenceL0 = referencesL0[i];
                        syntax.Partitions[p].ReferenceL1 = referencesL1[i];
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
                        ReadMvd(ref partition, list);
                    }
                }
            }
        }
        else if (syntax.Kind != MacroblockKind.BDirect16x16)
        {
            for (var list = 0; list < 2; list++)
            {
                var count = list == 0 ? countL0 : countL1;
                for (var p = 0; p < syntax.PartitionCount; p++)
                {
                    ref var partition = ref syntax.Partitions[p];
                    if (list == 0 && partition.UsesL0)
                    {
                        partition.ReferenceL0 = count > 1 ? ReadReferenceIndex(count) : 0;
                    }
                    else if (list == 1 && partition.UsesL1)
                    {
                        partition.ReferenceL1 = count > 1 ? ReadReferenceIndex(count) : 0;
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
                        ReadMvd(ref partition, list);
                    }
                }
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

    private void ReadMvd(ref InterPartition partition, int list)
    {
        var x = _reader.ReadSe(-8192, 8191, "mvd");
        var y = _reader.ReadSe(-8192, 8191, "mvd");
        if (list == 0)
        {
            partition.MvdL0X = x;
            partition.MvdL0Y = y;
        }
        else
        {
            partition.MvdL1X = x;
            partition.MvdL1Y = y;
        }
    }

    private void DecodeSkippedMacroblockB()
    {
        StartSkippedMacroblockB();
        ReconstructMacroblock();
    }

    private void DeriveDirectMotion(ref InterPartition partition)
    {
        if (_header.DirectSpatialMvPrediction)
        {
            DeriveSpatialDirect(ref partition);
        }
        else
        {
            DeriveTemporalDirect(ref partition);
        }
    }

    private void DeriveSpatialDirect(ref InterPartition partition)
    {
        if (!_spatialDirectReady)
        {
            PrepareSpatialDirect();
        }

        var colocatedZero = false;
        _ = ColocatedPicture;
        if (!_longTermL1[0])
        {
            var (reference, mvX, mvY, _) = ColocatedMotion(partition.X, partition.Y);
            colocatedZero = reference == 0 && mvX is >= -1 and <= 1 && mvY is >= -1 and <= 1;
        }

        partition.UsesL0 = _spatialReferenceL0 >= 0;
        partition.UsesL1 = _spatialReferenceL1 >= 0;
        partition.ReferenceL0 = _spatialReferenceL0;
        partition.ReferenceL1 = _spatialReferenceL1;
        if (partition.UsesL0)
        {
            var zero = _spatialDirectZero || (_spatialReferenceL0 == 0 && colocatedZero);
            AssignMotion(ref partition, 0, _spatialReferenceL0, zero ? 0 : _spatialMvL0X, zero ? 0 : _spatialMvL0Y);
        }

        if (partition.UsesL1)
        {
            var zero = _spatialDirectZero || (_spatialReferenceL1 == 0 && colocatedZero);
            AssignMotion(ref partition, 1, _spatialReferenceL1, zero ? 0 : _spatialMvL1X, zero ? 0 : _spatialMvL1Y);
        }
    }

    private void PrepareSpatialDirect()
    {
        _spatialDirectReady = true;
        Span<int> references = stackalloc int[2];
        for (var list = 0; list < 2; list++)
        {
            var a = NeighbourMotion(-1, 0, list);
            var b = NeighbourMotion(0, -1, list);
            var c = NeighbourMotion(4, -1, list);
            if (!c.Available)
            {
                c = NeighbourMotion(-1, -1, list);
            }

            references[list] = MinPositive(a.Reference, MinPositive(b.Reference, c.Reference));
        }

        _spatialDirectZero = references[0] < 0 && references[1] < 0;
        if (_spatialDirectZero)
        {
            references[0] = 0;
            references[1] = 0;
        }

        _spatialReferenceL0 = references[0];
        _spatialReferenceL1 = references[1];
        _spatialMvL0X = _spatialMvL0Y = _spatialMvL1X = _spatialMvL1Y = 0;
        if (_spatialDirectZero)
        {
            return;
        }

        var whole = new InterPartition { X = 0, Y = 0, Width = 4, Height = 4, ReferenceL0 = references[0], ReferenceL1 = references[1] };
        if (references[0] >= 0)
        {
            (_spatialMvL0X, _spatialMvL0Y) = PredictMotion(ref whole, 0);
        }

        if (references[1] >= 0)
        {
            (_spatialMvL1X, _spatialMvL1Y) = PredictMotion(ref whole, 1);
        }
    }

    private H264Picture ColocatedPicture =>
        _referencesL1.Length > 0 && _referencesL1[0] is { } picture
            ? picture
            : throw new InvalidDataException("H.264: a direct-predicted macroblock needs a picture in reference list 1");

    private static int MinPositive(int x, int y) => x >= 0 && y >= 0 ? Math.Min(x, y) : Math.Max(x, y);

    private void DeriveTemporalDirect(ref InterPartition partition)
    {
        var (referenceCol, mvColX, mvColY, pictureId) = ColocatedMotion(partition.X, partition.Y);
        var referenceL0 = 0;
        if (referenceCol >= 0)
        {
            referenceL0 = -1;
            for (var i = 0; i < _referencesL0.Length; i++)
            {
                if (_referencesL0[i] is { } candidate && candidate.PictureId == pictureId)
                {
                    referenceL0 = i;
                    break;
                }
            }

            if (referenceL0 < 0)
            {
                throw new InvalidDataException("H.264: the picture the co-located block refers to is not in reference list 0");
            }
        }

        if (_referencesL0[referenceL0] is null)
        {
            throw new InvalidDataException("H.264: a macroblock refers to a missing reference picture");
        }

        int mvL0X;
        int mvL0Y;
        int mvL1X;
        int mvL1Y;
        var distance = _pocL1[0] - _pocL0[referenceL0];
        if (_longTermL0[referenceL0] || distance == 0)
        {
            (mvL0X, mvL0Y, mvL1X, mvL1Y) = (mvColX, mvColY, 0, 0);
        }
        else
        {
            var scale = DistanceScaleFactor(_currentPoc - _pocL0[referenceL0], distance);
            mvL0X = ((scale * mvColX) + 128) >> 8;
            mvL0Y = ((scale * mvColY) + 128) >> 8;
            mvL1X = mvL0X - mvColX;
            mvL1Y = mvL0Y - mvColY;
        }

        partition.UsesL0 = true;
        partition.UsesL1 = true;
        partition.ReferenceL0 = referenceL0;
        partition.ReferenceL1 = 0;
        AssignMotion(ref partition, 0, referenceL0, mvL0X, mvL0Y);
        AssignMotion(ref partition, 1, 0, mvL1X, mvL1Y);
    }

    private static int DistanceScaleFactor(int currentDistance, int referenceDistance)
    {
        var tb = Math.Clamp(currentDistance, -128, 127);
        var td = Math.Clamp(referenceDistance, -128, 127);
        var tx = (16384 + Math.Abs(td / 2)) / td;
        return Math.Clamp(((tb * tx) + 32) >> 6, -1024, 1023);
    }

    private (int Reference, int MvX, int MvY, int PictureId) ColocatedMotion(int x, int y)
    {
        var colocated = ColocatedPicture;
        AwaitLines(colocated, 16 * (_mbY + 1));
        if (colocated.IsIntra(_macroblock))
        {
            return (-1, 0, 0, 0);
        }

        if (_sps.Direct8x8Inference)
        {
            x = (x >> 1) * 3;
            y = (y >> 1) * 3;
        }

        var slot = (_macroblock * 4) + ((y >> 1) * 2) + (x >> 1);
        var list = colocated.ReferenceIndexes[0][slot] >= 0 ? 0 : colocated.ReferenceIndexes[1][slot] >= 0 ? 1 : -1;
        if (list < 0)
        {
            return (-1, 0, 0, 0);
        }

        var index = (_macroblock * 16) + (y * 4) + x;
        var vectors = colocated.MotionVectors[list];
        return (colocated.ReferenceIndexes[list][slot], vectors[2 * index], vectors[(2 * index) + 1], colocated.ReferencePictureIds[list][slot]);
    }

    private void CombineBipredicted(int referenceL0, int referenceL1, Span<byte> luma, int lumaStride, Span<byte> cb, Span<byte> cr, int chromaStride, ReadOnlySpan<byte> luma1, ReadOnlySpan<byte> cb1, ReadOnlySpan<byte> cr1, int width, int height)
    {
        var chromaWidth = width / 2;
        var chromaHeight = height / 2;
        if (_header.Weights is { } weights)
        {
            var (lumaWeight0, lumaOffset0) = weights.Luma[0][referenceL0];
            var (lumaWeight1, lumaOffset1) = weights.Luma[1][referenceL1];
            Bipredict(luma, lumaStride, luma1, width, width, height, weights.LumaLog2Denominator, lumaWeight0, lumaWeight1, lumaOffset0, lumaOffset1);
            var chroma0 = weights.Chroma[0][referenceL0];
            var chroma1 = weights.Chroma[1][referenceL1];
            Bipredict(cb, chromaStride, cb1, chromaWidth, chromaWidth, chromaHeight, weights.ChromaLog2Denominator, chroma0[0].Weight, chroma1[0].Weight, chroma0[0].Offset, chroma1[0].Offset);
            Bipredict(cr, chromaStride, cr1, chromaWidth, chromaWidth, chromaHeight, weights.ChromaLog2Denominator, chroma0[1].Weight, chroma1[1].Weight, chroma0[1].Offset, chroma1[1].Offset);
            return;
        }

        if (_pps.WeightedBipredictionIdc == 2 && ImplicitWeights(referenceL0, referenceL1) is var (weight0, weight1) && weight0 != weight1)
        {
            InterPrediction.BipredictImplicit(luma, lumaStride, luma1, width, width, height, weight0, weight1);
            InterPrediction.BipredictImplicit(cb, chromaStride, cb1, chromaWidth, chromaWidth, chromaHeight, weight0, weight1);
            InterPrediction.BipredictImplicit(cr, chromaStride, cr1, chromaWidth, chromaWidth, chromaHeight, weight0, weight1);
            return;
        }

        InterPrediction.AverageInto(luma, lumaStride, luma1, width, width, height);
        InterPrediction.AverageInto(cb, chromaStride, cb1, chromaWidth, chromaWidth, chromaHeight);
        InterPrediction.AverageInto(cr, chromaStride, cr1, chromaWidth, chromaWidth, chromaHeight);
    }

    private bool HasUniformDirectMotion()
    {
        var syntax = _syntax;
        var first = syntax.Partitions[0];
        for (var p = 1; p < syntax.PartitionCount; p++)
        {
            ref var partition = ref syntax.Partitions[p];
            if (partition.UsesL0 != first.UsesL0 || partition.UsesL1 != first.UsesL1 || partition.ReferenceL0 != first.ReferenceL0 || partition.ReferenceL1 != first.ReferenceL1)
            {
                return false;
            }
        }

        for (var list = 0; list < 2; list++)
        {
            if (list == 0 ? !first.UsesL0 : !first.UsesL1)
            {
                continue;
            }

            var vectors = _picture.MotionVectors[list].AsSpan(_macroblock * 32, 32);
            var reference = System.Runtime.InteropServices.MemoryMarshal.Cast<short, int>(vectors);
            if (reference.IndexOfAnyExcept(reference[0]) >= 0)
            {
                return false;
            }
        }

        return true;
    }

    private static void Bipredict(Span<byte> first, int firstStride, ReadOnlySpan<byte> second, int secondStride, int width, int height, int log2Denominator, int weight0, int weight1, int offset0, int offset1)
    {
        if (weight0 == 1 << log2Denominator && weight1 == weight0 && offset0 == 0 && offset1 == 0)
        {
            InterPrediction.AverageInto(first, firstStride, second, secondStride, width, height);
            return;
        }

        InterPrediction.Bipredict(first, firstStride, second, secondStride, width, height, log2Denominator, weight0, weight1, offset0, offset1);
    }

    private (int Weight0, int Weight1) ImplicitWeights(int referenceL0, int referenceL1)
    {
        var distance = _pocL1[referenceL1] - _pocL0[referenceL0];
        if (distance == 0 || _longTermL0[referenceL0] || _longTermL1[referenceL1])
        {
            return (32, 32);
        }

        var scale = DistanceScaleFactor(_currentPoc - _pocL0[referenceL0], distance) >> 2;
        return scale is < -64 or > 128 ? (32, 32) : (64 - scale, scale);
    }
}
