namespace EggEncoder.Codecs.H264;

internal readonly record struct SliceFilterParameters(int DisableIdc, int OffsetA, int OffsetB, int ChromaQpOffsetCb, int ChromaQpOffsetCr);

internal static class DeblockingFilter
{
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

    public static void Apply(H264Picture picture, IReadOnlyList<SliceFilterParameters> slices)
    {
        var width = picture.WidthInMacroblocks;
        var height = picture.HeightInMacroblocks;
        Span<byte> strengths = stackalloc byte[16];
        for (var macroblock = 0; macroblock < width * height; macroblock++)
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

            for (var vertical = 1; vertical >= 0; vertical--)
            {
                var neighbour = vertical == 1 ? left : top;
                for (var edge = 0; edge < 4; edge++)
                {
                    if (edge == 0 && neighbour < 0)
                    {
                        continue;
                    }

                    if (edge % 2 == 1 && picture.Transform8x8[macroblock])
                    {
                        continue;
                    }

                    var any = false;
                    for (var k = 0; k < 4; k++)
                    {
                        strengths[k] = BoundaryStrength(picture, macroblock, edge == 0 ? neighbour : macroblock, vertical == 1, edge, k);
                        any |= strengths[k] != 0;
                    }

                    if (!any)
                    {
                        continue;
                    }

                    var pMacroblock = edge == 0 ? neighbour : macroblock;
                    FilterLumaEdge(picture, macroblock, pMacroblock, vertical == 1, edge, strengths[..4], parameters);
                    if (edge % 2 == 0)
                    {
                        FilterChromaEdge(picture, macroblock, pMacroblock, vertical == 1, edge / 2, strengths[..4], parameters);
                    }
                }
            }
        }
    }

    private static byte BoundaryStrength(H264Picture picture, int q, int p, bool vertical, int edge, int k)
    {
        var macroblockEdge = edge == 0;
        if (picture.IsIntra(q) || picture.IsIntra(p))
        {
            return (byte)(macroblockEdge ? 4 : 3);
        }

        var qx = vertical ? edge : k;
        var qy = vertical ? k : edge;
        var px = vertical ? (macroblockEdge ? 3 : edge - 1) : k;
        var py = vertical ? k : (macroblockEdge ? 3 : edge - 1);
        if (HasCoefficients(picture, q, qx, qy) || HasCoefficients(picture, p, px, py))
        {
            return 2;
        }

        return MotionDiffers(picture, q, (qy * 4) + qx, p, (py * 4) + px) ? (byte)1 : (byte)0;
    }

    private static bool HasCoefficients(H264Picture picture, int macroblock, int x, int y)
    {
        if (picture.Transform8x8[macroblock])
        {
            var x8 = x & ~1;
            var y8 = y & ~1;
            var start = macroblock * 16;
            return picture.TotalCoefficientsLuma[start + (y8 * 4) + x8] != 0
                || picture.TotalCoefficientsLuma[start + (y8 * 4) + x8 + 1] != 0
                || picture.TotalCoefficientsLuma[start + ((y8 + 1) * 4) + x8] != 0
                || picture.TotalCoefficientsLuma[start + ((y8 + 1) * 4) + x8 + 1] != 0;
        }

        return picture.TotalCoefficientsLuma[(macroblock * 16) + (y * 4) + x] != 0;
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
        int p0 = buffer[q0Index - step];
        int p1 = buffer[q0Index - (2 * step)];
        int q0 = buffer[q0Index];
        int q1 = buffer[q0Index + step];
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
                buffer[q0Index - step] = IntraPrediction.Clip(p0 + delta);
                buffer[q0Index] = IntraPrediction.Clip(q0 - delta);
            }
            else
            {
                buffer[q0Index - step] = (byte)(((2 * p1) + p0 + q1 + 2) >> 2);
                buffer[q0Index] = (byte)(((2 * q1) + q0 + p1 + 2) >> 2);
            }

            return;
        }

        int p2 = buffer[q0Index - (3 * step)];
        int q2 = buffer[q0Index + (2 * step)];
        var ap = Math.Abs(p2 - p0);
        var aq = Math.Abs(q2 - q0);
        if (bS < 4)
        {
            var tc = tc0 + (ap < beta ? 1 : 0) + (aq < beta ? 1 : 0);
            var delta = Math.Clamp((((q0 - p0) << 2) + (p1 - q1) + 4) >> 3, -tc, tc);
            buffer[q0Index - step] = IntraPrediction.Clip(p0 + delta);
            buffer[q0Index] = IntraPrediction.Clip(q0 - delta);
            if (ap < beta)
            {
                buffer[q0Index - (2 * step)] = (byte)(p1 + Math.Clamp((p2 + ((p0 + q0 + 1) >> 1) - (p1 << 1)) >> 1, -tc0, tc0));
            }

            if (aq < beta)
            {
                buffer[q0Index + step] = (byte)(q1 + Math.Clamp((q2 + ((p0 + q0 + 1) >> 1) - (q1 << 1)) >> 1, -tc0, tc0));
            }

            return;
        }

        int p3 = buffer[q0Index - (4 * step)];
        int q3 = buffer[q0Index + (3 * step)];
        var strong = Math.Abs(p0 - q0) < ((alpha >> 2) + 2);
        if (ap < beta && strong)
        {
            buffer[q0Index - step] = (byte)((p2 + (2 * p1) + (2 * p0) + (2 * q0) + q1 + 4) >> 3);
            buffer[q0Index - (2 * step)] = (byte)((p2 + p1 + p0 + q0 + 2) >> 2);
            buffer[q0Index - (3 * step)] = (byte)(((2 * p3) + (3 * p2) + p1 + p0 + q0 + 4) >> 3);
        }
        else
        {
            buffer[q0Index - step] = (byte)(((2 * p1) + p0 + q1 + 2) >> 2);
        }

        if (aq < beta && strong)
        {
            buffer[q0Index] = (byte)((p1 + (2 * p0) + (2 * q0) + (2 * q1) + q2 + 4) >> 3);
            buffer[q0Index + step] = (byte)((p0 + q0 + q1 + q2 + 2) >> 2);
            buffer[q0Index + (2 * step)] = (byte)(((2 * q3) + (3 * q2) + q1 + q0 + p0 + 4) >> 3);
        }
        else
        {
            buffer[q0Index] = (byte)(((2 * q1) + q0 + p1 + 2) >> 2);
        }
    }
}
