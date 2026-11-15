namespace EggEncoder.Codecs.H264;

internal sealed class MacroblockSyntax
{
    public MacroblockKind Kind;

    public int Intra16x16PredictionMode;

    public int IntraChromaPredictionMode;

    public int CodedBlockPatternLuma;

    public int CodedBlockPatternChroma;

    public int QpDelta;

    public bool Transform8x8;

    public readonly bool[] PreviousIntraModeFlags = new bool[16];

    public readonly byte[] RemainingIntraModes = new byte[16];

    public readonly short[] LumaCoefficients = new short[256];

    public readonly short[] LumaDc = new short[16];

    public readonly short[] ChromaDc = new short[8];

    public readonly short[] ChromaAc = new short[128];

    public readonly byte[] PcmSamples = new byte[384];

    public readonly bool[] LumaBlockCoded = new bool[16];

    public readonly bool[] ChromaAcBlockCoded = new bool[8];

    public bool LumaDcCoded;

    public InterShape Shape;

    public int PartitionCount;

    public readonly InterPartition[] Partitions = new InterPartition[16];

    public readonly int[] SubMacroblockTypes = new int[4];

    public readonly bool[] ChromaDcCoded = new bool[2];

    public void ClearResidual()
    {
        Array.Clear(LumaCoefficients);
        Array.Clear(LumaDc);
        Array.Clear(ChromaDc);
        Array.Clear(ChromaAc);
        Array.Clear(LumaBlockCoded);
        Array.Clear(ChromaAcBlockCoded);
        LumaDcCoded = false;
        ChromaDcCoded[0] = false;
        ChromaDcCoded[1] = false;
    }
}

internal enum InterShape
{
    Partition16x16,
    Partition16x8,
    Partition8x16,
    Partition8x8
}

internal struct InterPartition
{
    public int X;
    public int Y;
    public int Width;
    public int Height;
    public int PartitionIndex;
    public int ReferenceL0;
    public int ReferenceL1;
    public int MvdL0X;
    public int MvdL0Y;
    public int MvdL1X;
    public int MvdL1Y;
    public bool UsesL0;
    public bool UsesL1;
}

internal static class MacroblockLayout
{
    public static readonly int[] BlockX = [0, 1, 0, 1, 2, 3, 2, 3, 0, 1, 0, 1, 2, 3, 2, 3];

    public static readonly int[] BlockY = [0, 0, 1, 1, 0, 0, 1, 1, 2, 2, 3, 3, 2, 2, 3, 3];

    public static readonly int[] RasterToBlock = BuildRasterToBlock();

    public static readonly byte[] ZigZag4x4 = [0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15];

    public static readonly byte[] ZigZag8x8 =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63
    ];

    public static readonly int[] IntraCodedBlockPattern =
    [
        47, 31, 15, 0, 23, 27, 29, 30, 7, 11, 13, 14, 39, 43, 45, 46,
        16, 3, 5, 10, 12, 19, 21, 26, 28, 35, 37, 42, 44, 1, 2, 4,
        8, 17, 18, 20, 24, 6, 9, 22, 25, 32, 33, 34, 36, 40, 38, 41
    ];

    public static readonly int[] InterCodedBlockPattern =
    [
        0, 16, 1, 2, 4, 8, 32, 3, 5, 10, 12, 15, 47, 7, 11, 13,
        14, 6, 9, 31, 35, 37, 42, 44, 33, 34, 36, 40, 39, 43, 45, 46,
        17, 18, 20, 24, 19, 21, 26, 28, 23, 27, 29, 30, 22, 25, 38, 41
    ];

    private static int[] BuildRasterToBlock()
    {
        var map = new int[16];
        for (var block = 0; block < 16; block++)
        {
            map[(BlockY[block] * 4) + BlockX[block]] = block;
        }

        return map;
    }
}
