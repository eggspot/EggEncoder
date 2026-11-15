using EggEncoder.Video;

namespace EggEncoder.Codecs.H264;

internal enum MacroblockKind : byte
{
    NotDecoded = 0,
    IntraNxN,
    Intra16x16,
    IntraPcm,
    PSkip,
    P16x16,
    P16x8,
    P8x16,
    P8x8,
    BSkip,
    BDirect16x16,
    B16x16,
    B16x8,
    B8x16,
    B8x8
}

internal sealed class H264Picture : IDisposable
{
    public const int Padding = 32;

    public H264Picture(VideoFramePool pool, SequenceParameterSet sps)
    {
        WidthInMacroblocks = sps.WidthInMacroblocks;
        HeightInMacroblocks = sps.HeightInMacroblocks;
        var count = WidthInMacroblocks * HeightInMacroblocks;
        Frame = pool.Rent(WidthInMacroblocks * 16, HeightInMacroblocks * 16, sps.Range, sps.ColorMatrix, Padding);
        Kinds = new MacroblockKind[count];
        SliceNumbers = new int[count];
        QpY = new sbyte[count];
        Transform8x8 = new bool[count];
        IntraModes = new byte[count * 16];
        TotalCoefficientsLuma = new byte[count * 16];
        TotalCoefficientsChroma = new byte[count * 8];
        MotionVectors = new short[2][];
        ReferenceIndexes = new sbyte[2][];
        ReferencePictureIds = new int[2][];
        for (var list = 0; list < 2; list++)
        {
            MotionVectors[list] = new short[count * 32];
            ReferenceIndexes[list] = new sbyte[count * 4];
            ReferencePictureIds[list] = new int[count * 4];
        }
    }

    public VideoFrame Frame { get; }

    public int WidthInMacroblocks { get; }

    public int HeightInMacroblocks { get; }

    public MacroblockKind[] Kinds { get; }

    public int[] SliceNumbers { get; }

    public sbyte[] QpY { get; }

    public bool[] Transform8x8 { get; }

    public byte[] IntraModes { get; }

    public byte[] TotalCoefficientsLuma { get; }

    public byte[] TotalCoefficientsChroma { get; }

    public short[][] MotionVectors { get; }

    public sbyte[][] ReferenceIndexes { get; }

    public int[][] ReferencePictureIds { get; }

    public int PictureId { get; set; }

    public int FrameNum { get; set; }

    public int FrameNumWrap { get; set; }

    public int PicOrderCount { get; set; }

    public int LongTermFrameIndex { get; set; } = -1;

    public bool IsReference { get; set; }

    public bool IsLongTerm { get; set; }

    public bool IsIdr { get; set; }

    public bool NeedsOutput { get; set; }

    public bool HasMemoryManagement5 { get; set; }

    public TimeSpan Timestamp { get; set; }

    public bool IsIntra(int macroblock) => Kinds[macroblock] is MacroblockKind.IntraNxN or MacroblockKind.Intra16x16 or MacroblockKind.IntraPcm;

    public void ResetMacroblocks()
    {
        Array.Clear(Kinds);
        Array.Fill(SliceNumbers, -1);
    }

    public void Dispose() => Frame.Dispose();
}
