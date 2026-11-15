using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EggEncoder.Codecs.H264;

internal readonly record struct SliceFilterParameters(int DisableIdc, int OffsetA, int OffsetB, int ChromaQpOffsetCb, int ChromaQpOffsetCr);

[SkipLocalsInit]
internal static partial class DeblockingFilter
{
    private const int UniformMotion = 1 << 16;

    private static readonly byte[] Alpha =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4, 4, 5, 6, 7, 8, 9, 10, 12, 13,
        15, 17, 20, 22, 25, 28, 32, 36, 40, 45, 50, 56, 63, 71, 80, 90, 101, 113, 127, 144, 162, 182, 203, 226, 255, 255
    ];

    private static readonly byte[] Beta =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4,
        6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14, 15, 15, 16, 16, 17, 17, 18, 18
    ];

    private static readonly byte[][] Tc0 =
    [
        [
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1,
            1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 6, 6, 7, 8, 9, 10, 11, 13
        ],
        [
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1,
            1, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 4, 4, 5, 5, 6, 7, 8, 8, 10, 11, 12, 13, 15, 17
        ],
        [
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1,
            1, 2, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 6, 6, 7, 8, 9, 10, 11, 13, 14, 16, 18, 20, 23, 25
        ]
    ];

    public static void Apply(H264Picture picture, IReadOnlyList<SliceFilterParameters> slices) =>
        ApplyRows(picture, slices, 0, picture.HeightInMacroblocks);

    public static void ApplyRows(H264Picture picture, IReadOnlyList<SliceFilterParameters> slices, int firstRow, int endRow)
    {
        var width = picture.WidthInMacroblocks;
        var count = width * picture.HeightInMacroblocks;
        var masks = ArrayPool<int>.Shared.Rent(count);
        try
        {
            for (var macroblock = Math.Max(0, firstRow - 1) * width; macroblock < endRow * width; macroblock++)
            {
                masks[macroblock] = CoefficientMask(picture, macroblock) | (HasUniformMotion(picture, macroblock) ? UniformMotion : 0);
            }

            Span<byte> strengths = stackalloc byte[4];
            for (var macroblock = firstRow * width; macroblock < endRow * width; macroblock++)
            {
                var slice = picture.SliceNumbers[macroblock];
                if (slice < 0)
                {
                    continue;
                }

                var parameters = slices[slice];
                if (parameters.DisableIdc == 1)
                {
                    continue;
                }

                var mbX = macroblock % width;
                var mbY = macroblock / width;
                var left = mbX > 0 ? macroblock - 1 : -1;
                var top = mbY > 0 ? macroblock - width : -1;
                if (left >= 0 && (picture.SliceNumbers[left] < 0 || (parameters.DisableIdc == 2 && picture.SliceNumbers[left] != slice)))
                {
                    left = -1;
                }

                if (top >= 0 && (picture.SliceNumbers[top] < 0 || (parameters.DisableIdc == 2 && picture.SliceNumbers[top] != slice)))
                {
                    top = -1;
                }

                var intra = picture.IsIntra(macroblock);
                var transform8x8 = picture.Transform8x8[macroblock];
                var mask = masks[macroblock];
                var skipInternal = mask == UniformMotion;
                for (var vertical = 1; vertical >= 0; vertical--)
                {
                    var neighbour = vertical == 1 ? left : top;
                    for (var edge = 0; edge < 4; edge++)
                    {
                        if (edge == 0 ? neighbour < 0 : skipInternal || (transform8x8 && (edge & 1) == 1))
                        {
                            continue;
                        }

                        var p = edge == 0 ? neighbour : macroblock;
                        if (intra || (edge == 0 && picture.IsIntra(p)))
                        {
                            strengths.Fill((byte)(edge == 0 ? 4 : 3));
                        }
                        else if (!EdgeStrengths(picture, macroblock, mask, p, masks[p], vertical == 1, edge, strengths))
                        {
                            continue;
                        }

                        FilterLumaEdge(picture, macroblock, p, vertical == 1, edge, strengths, parameters);
                        if ((edge & 1) == 0)
                        {
                            FilterChromaEdge(picture, macroblock, p, vertical == 1, edge / 2, strengths, parameters);
                        }
                    }
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(masks);
        }
    }

    private static bool HasUniformMotion(H264Picture picture, int macroblock)
    {
        switch (picture.Kinds[macroblock])
        {
            case MacroblockKind.PSkip or MacroblockKind.P16x16 or MacroblockKind.B16x16:
                return true;
            case MacroblockKind.IntraNxN or MacroblockKind.Intra16x16 or MacroblockKind.IntraPcm or MacroblockKind.NotDecoded:
                return false;
        }

        for (var list = 0; list < 2; list++)
        {
            var references = picture.ReferenceIndexes[list].AsSpan(macroblock * 4, 4);
            if (references.IndexOfAnyExcept(references[0]) >= 0)
            {
                return false;
            }

            var pictureIds = picture.ReferencePictureIds[list].AsSpan(macroblock * 4, 4);
            if (references[0] >= 0 && pictureIds.IndexOfAnyExcept(pictureIds[0]) >= 0)
            {
                return false;
            }

            var vectors = MemoryMarshal.Cast<short, int>(picture.MotionVectors[list].AsSpan(macroblock * 32, 32));
            if (references[0] >= 0 && vectors.IndexOfAnyExcept(vectors[0]) >= 0)
            {
                return false;
            }
        }

        return true;
    }

    private static int CoefficientMask(H264Picture picture, int macroblock)
    {
        var totals = picture.TotalCoefficientsLuma.AsSpan(macroblock * 16, 16);
        var mask = 0;
        for (var i = 0; i < 16; i++)
        {
            if (totals[i] != 0)
            {
                mask |= 1 << i;
            }
        }

        if (picture.Transform8x8[macroblock])
        {
            var expanded = 0;
            for (var block8 = 0; block8 < 4; block8++)
            {
                var corner = ((block8 >> 1) * 8) + ((block8 & 1) * 2);
                var quad = (1 << corner) | (1 << (corner + 1)) | (1 << (corner + 4)) | (1 << (corner + 5));
                if ((mask & quad) != 0)
                {
                    expanded |= quad;
                }
            }

            mask = expanded;
        }

        return mask;
    }

    private static bool EdgeStrengths(H264Picture picture, int q, int qMask, int p, int pMask, bool vertical, int edge, Span<byte> strengths)
    {
        var any = false;
        var pEdge = edge == 0 ? 3 : edge - 1;
        if ((qMask & UniformMotion) != 0 && (edge != 0 || (pMask & UniformMotion) != 0))
        {
            var motion = edge == 0 && !SameMotion(picture, q, 0, p, 0) && MotionDiffers(picture, q, 0, p, 0) ? (byte)1 : (byte)0;
            for (var k = 0; k < 4; k++)
            {
                var qBlock = vertical ? (k * 4) + edge : (edge * 4) + k;
                var pBlock = vertical ? (k * 4) + pEdge : (pEdge * 4) + k;
                var strength = ((qMask >> qBlock) & 1) != 0 || ((pMask >> pBlock) & 1) != 0 ? (byte)2 : motion;
                strengths[k] = strength;
                any |= strength != 0;
            }

            return any;
        }

        for (var k = 0; k < 4; k++)
        {
            var qBlock = vertical ? (k * 4) + edge : (edge * 4) + k;
            var pBlock = vertical ? (k * 4) + pEdge : (pEdge * 4) + k;
            byte strength;
            if (((qMask >> qBlock) & 1) != 0 || ((pMask >> pBlock) & 1) != 0)
            {
                strength = 2;
            }
            else
            {
                strength = !SameMotion(picture, q, qBlock, p, pBlock) && MotionDiffers(picture, q, qBlock, p, pBlock) ? (byte)1 : (byte)0;
            }

            strengths[k] = strength;
            any |= strength != 0;
        }

        return any;
    }

    private static bool SameMotion(H264Picture picture, int q, int qBlock, int p, int pBlock)
    {
        var qPartition = (q * 4) + ((qBlock >> 3) * 2) + ((qBlock & 3) >> 1);
        var pPartition = (p * 4) + ((pBlock >> 3) * 2) + ((pBlock & 3) >> 1);
        var qVector = (q * 16) + qBlock;
        var pVector = (p * 16) + pBlock;
        for (var list = 0; list < 2; list++)
        {
            var qReference = picture.ReferenceIndexes[list][qPartition];
            var pReference = picture.ReferenceIndexes[list][pPartition];
            if ((qReference < 0) != (pReference < 0))
            {
                return false;
            }

            if (qReference < 0)
            {
                continue;
            }

            if (picture.ReferencePictureIds[list][qPartition] != picture.ReferencePictureIds[list][pPartition])
            {
                return false;
            }

            var vectors = MemoryMarshal.Cast<short, int>(picture.MotionVectors[list]);
            if (vectors[qVector] != vectors[pVector])
            {
                return false;
            }
        }

        return true;
    }

    private static bool MotionDiffers(H264Picture picture, int q, int qBlock, int p, int pBlock)
    {
        var qPartition = (q * 4) + ((qBlock >> 3) * 2) + ((qBlock & 3) >> 1);
        var pPartition = (p * 4) + ((pBlock >> 3) * 2) + ((pBlock & 3) >> 1);
        var q0 = picture.ReferenceIndexes[0][qPartition] >= 0 ? picture.ReferencePictureIds[0][qPartition] : -1;
        var q1 = picture.ReferenceIndexes[1][qPartition] >= 0 ? picture.ReferencePictureIds[1][qPartition] : -1;
        var p0 = picture.ReferenceIndexes[0][pPartition] >= 0 ? picture.ReferencePictureIds[0][pPartition] : -1;
        var p1 = picture.ReferenceIndexes[1][pPartition] >= 0 ? picture.ReferencePictureIds[1][pPartition] : -1;
        var qCount = (q0 >= 0 ? 1 : 0) + (q1 >= 0 ? 1 : 0);
        var pCount = (p0 >= 0 ? 1 : 0) + (p1 >= 0 ? 1 : 0);
        if (qCount != pCount)
        {
            return true;
        }

        if (!((q0 == p0 && q1 == p1) || (q0 == p1 && q1 == p0)))
        {
            return true;
        }

        var qMv = (q * 16) + qBlock;
        var pMv = (p * 16) + pBlock;
        if (qCount == 1)
        {
            var qList = q0 >= 0 ? 0 : 1;
            var pList = p0 >= 0 ? 0 : 1;
            return VectorsFar(picture, qList, qMv, pList, pMv);
        }

        if (q0 != q1)
        {
            return q0 == p0
                ? VectorsFar(picture, 0, qMv, 0, pMv) || VectorsFar(picture, 1, qMv, 1, pMv)
                : VectorsFar(picture, 0, qMv, 1, pMv) || VectorsFar(picture, 1, qMv, 0, pMv);
        }

        return (VectorsFar(picture, 0, qMv, 0, pMv) || VectorsFar(picture, 1, qMv, 1, pMv))
            && (VectorsFar(picture, 0, qMv, 1, pMv) || VectorsFar(picture, 1, qMv, 0, pMv));
    }

    private static bool VectorsFar(H264Picture picture, int qList, int qIndex, int pList, int pIndex)
    {
        var q = picture.MotionVectors[qList];
        var p = picture.MotionVectors[pList];
        return Math.Abs(q[2 * qIndex] - p[2 * pIndex]) >= 4 || Math.Abs(q[(2 * qIndex) + 1] - p[(2 * pIndex) + 1]) >= 4;
    }

    private static void FilterLumaEdge(H264Picture picture, int q, int p, bool vertical, int edge, ReadOnlySpan<byte> strengths, SliceFilterParameters parameters)
    {
        var frame = picture.Frame;
        var buffer = frame.Buffer;
        var stride = frame.StrideY;
        var width = picture.WidthInMacroblocks;
        var origin = frame.OffsetY + ((q / width) * 16 * stride) + ((q % width) * 16);
        var qpP = picture.Kinds[p] == MacroblockKind.IntraPcm ? 0 : picture.QpY[p];
        var qpQ = picture.Kinds[q] == MacroblockKind.IntraPcm ? 0 : picture.QpY[q];
        var average = (qpP + qpQ + 1) >> 1;
        var indexA = Math.Clamp(average + parameters.OffsetA, 0, 51);
        var indexB = Math.Clamp(average + parameters.OffsetB, 0, 51);
        int alpha = Alpha[indexA];
        int beta = Beta[indexB];
        if (alpha == 0 || beta == 0)
        {
            return;
        }

        var step = vertical ? 1 : stride;
        var along = vertical ? stride : 1;
        var start = origin + (vertical ? edge * 4 : edge * 4 * stride);
        if (CanFilterVectors)
        {
            FilterLumaVectors(buffer, start, stride, vertical, strengths, alpha, beta, indexA);
            return;
        }

        for (var i = 0; i < 16; i++)
        {
            var bS = strengths[i >> 2];
            if (bS == 0)
            {
                continue;
            }

            FilterSamples(buffer, start + (i * along), step, bS, alpha, beta, bS < 4 ? Tc0[bS - 1][indexA] : 0, chroma: false);
        }
    }

    private static void FilterChromaEdge(H264Picture picture, int q, int p, bool vertical, int edge, ReadOnlySpan<byte> strengths, SliceFilterParameters parameters)
    {
        var frame = picture.Frame;
        var buffer = frame.Buffer;
        var stride = frame.StrideUV;
        var width = picture.WidthInMacroblocks;
        var step = vertical ? 1 : stride;
        var along = vertical ? stride : 1;
        for (var plane = 0; plane < 2; plane++)
        {
            var offset = plane == 0 ? parameters.ChromaQpOffsetCb : parameters.ChromaQpOffsetCr;
            var qpP = Dequantizer.ChromaQp(picture.Kinds[p] == MacroblockKind.IntraPcm ? 0 : picture.QpY[p], offset);
            var qpQ = Dequantizer.ChromaQp(picture.Kinds[q] == MacroblockKind.IntraPcm ? 0 : picture.QpY[q], offset);
            var average = (qpP + qpQ + 1) >> 1;
            var indexA = Math.Clamp(average + parameters.OffsetA, 0, 51);
            var indexB = Math.Clamp(average + parameters.OffsetB, 0, 51);
            int alpha = Alpha[indexA];
            int beta = Beta[indexB];
            if (alpha == 0 || beta == 0)
            {
                continue;
            }

            var origin = (plane == 0 ? frame.OffsetU : frame.OffsetV) + ((q / width) * 8 * stride) + ((q % width) * 8);
            var start = origin + (vertical ? edge * 4 : edge * 4 * stride);
            if (CanFilterVectors)
            {
                FilterChromaVectors(buffer, start, stride, vertical, strengths, alpha, beta, indexA);
                continue;
            }

            for (var i = 0; i < 8; i++)
            {
                var bS = strengths[i >> 1];
                if (bS == 0)
                {
                    continue;
                }

                FilterSamples(buffer, start + (i * along), step, bS, alpha, beta, bS < 4 ? Tc0[bS - 1][indexA] : 0, chroma: true);
            }
        }
    }

    private static void FilterSamples(byte[] buffer, int q0Index, int step, int bS, int alpha, int beta, int tc0, bool chroma)
    {
        ref var q0Ref = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(buffer), q0Index);
        int p0 = Unsafe.Add(ref q0Ref, -step);
        int p1 = Unsafe.Add(ref q0Ref, -2 * step);
        int q0 = q0Ref;
        int q1 = Unsafe.Add(ref q0Ref, step);
        if (Math.Abs(p0 - q0) >= alpha || Math.Abs(p1 - p0) >= beta || Math.Abs(q1 - q0) >= beta)
        {
            return;
        }

        if (chroma)
        {
            if (bS < 4)
            {
                var tc = tc0 + 1;
                var delta = Math.Clamp((((q0 - p0) << 2) + (p1 - q1) + 4) >> 3, -tc, tc);
                Unsafe.Add(ref q0Ref, -step) = IntraPrediction.Clip(p0 + delta);
                q0Ref = IntraPrediction.Clip(q0 - delta);
            }
            else
            {
                Unsafe.Add(ref q0Ref, -step) = (byte)(((2 * p1) + p0 + q1 + 2) >> 2);
                q0Ref = (byte)(((2 * q1) + q0 + p1 + 2) >> 2);
            }

            return;
        }

        int p2 = Unsafe.Add(ref q0Ref, -3 * step);
        int q2 = Unsafe.Add(ref q0Ref, 2 * step);
        var ap = Math.Abs(p2 - p0);
        var aq = Math.Abs(q2 - q0);
        if (bS < 4)
        {
            var tc = tc0 + (ap < beta ? 1 : 0) + (aq < beta ? 1 : 0);
            var delta = Math.Clamp((((q0 - p0) << 2) + (p1 - q1) + 4) >> 3, -tc, tc);
            Unsafe.Add(ref q0Ref, -step) = IntraPrediction.Clip(p0 + delta);
            q0Ref = IntraPrediction.Clip(q0 - delta);
            if (ap < beta)
            {
                Unsafe.Add(ref q0Ref, -2 * step) = (byte)(p1 + Math.Clamp((p2 + ((p0 + q0 + 1) >> 1) - (p1 << 1)) >> 1, -tc0, tc0));
            }

            if (aq < beta)
            {
                Unsafe.Add(ref q0Ref, step) = (byte)(q1 + Math.Clamp((q2 + ((p0 + q0 + 1) >> 1) - (q1 << 1)) >> 1, -tc0, tc0));
            }

            return;
        }

        int p3 = Unsafe.Add(ref q0Ref, -4 * step);
        int q3 = Unsafe.Add(ref q0Ref, 3 * step);
        var strong = Math.Abs(p0 - q0) < ((alpha >> 2) + 2);
        if (ap < beta && strong)
        {
            Unsafe.Add(ref q0Ref, -step) = (byte)((p2 + (2 * p1) + (2 * p0) + (2 * q0) + q1 + 4) >> 3);
            Unsafe.Add(ref q0Ref, -2 * step) = (byte)((p2 + p1 + p0 + q0 + 2) >> 2);
            Unsafe.Add(ref q0Ref, -3 * step) = (byte)(((2 * p3) + (3 * p2) + p1 + p0 + q0 + 4) >> 3);
        }
        else
        {
            Unsafe.Add(ref q0Ref, -step) = (byte)(((2 * p1) + p0 + q1 + 2) >> 2);
        }

        if (aq < beta && strong)
        {
            q0Ref = (byte)((p1 + (2 * p0) + (2 * q0) + (2 * q1) + q2 + 4) >> 3);
            Unsafe.Add(ref q0Ref, step) = (byte)((p0 + q0 + q1 + q2 + 2) >> 2);
            Unsafe.Add(ref q0Ref, 2 * step) = (byte)(((2 * q3) + (3 * q2) + q1 + q0 + p0 + 4) >> 3);
        }
        else
        {
            q0Ref = (byte)(((2 * q1) + q0 + p1 + 2) >> 2);
        }
    }
}
