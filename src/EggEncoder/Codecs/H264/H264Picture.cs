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

internal sealed class MacroblockArrays
{
    public MacroblockArrays(int count)
    {
        Count = count;
        Kinds = new MacroblockKind[count];
        SliceNumbers = new int[count];
        QpY = new sbyte[count];
        Transform8x8 = new bool[count];
        IntraModes = new byte[count * 16];
        TotalCoefficientsLuma = new byte[count * 16];
        TotalCoefficientsChroma = new byte[count * 8];
        MotionVectors = [new short[count * 32], new short[count * 32]];
        ReferenceIndexes = [new sbyte[count * 4], new sbyte[count * 4]];
        ReferencePictureIds = [new int[count * 4], new int[count * 4]];
    }

    public int Count { get; }

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
}

internal sealed class MacroblockArrayPool
{
    private const int MaxRetained = 20;

    private readonly Stack<MacroblockArrays> _free = new();

    public MacroblockArrays Rent(int count)
    {
        while (_free.TryPop(out var arrays))
        {
            if (arrays.Count == count)
            {
                return arrays;
            }
        }

        return new MacroblockArrays(count);
    }

    public void Return(MacroblockArrays arrays)
    {
        if (_free.Count < MaxRetained)
        {
            _free.Push(arrays);
        }
    }
}

internal sealed class H264Picture : IDisposable
{
    public const int Padding = 32;

    private readonly MacroblockArrays _arrays;
    private readonly MacroblockArrayPool? _arrayPool;
    private int _holds = 1;
    private bool _disposed;

    public H264Picture(VideoFramePool pool, SequenceParameterSet sps, MacroblockArrayPool? arrayPool = null)
    {
        Sequence = sps;
        WidthInMacroblocks = sps.WidthInMacroblocks;
        HeightInMacroblocks = sps.HeightInMacroblocks;
        var count = WidthInMacroblocks * HeightInMacroblocks;
        Frame = pool.Rent(WidthInMacroblocks * 16, HeightInMacroblocks * 16, sps.Range, sps.ColorMatrix, Padding);
        _arrayPool = arrayPool;
        _arrays = arrayPool?.Rent(count) ?? new MacroblockArrays(count);
        Kinds = _arrays.Kinds;
        SliceNumbers = _arrays.SliceNumbers;
        QpY = _arrays.QpY;
        Transform8x8 = _arrays.Transform8x8;
        IntraModes = _arrays.IntraModes;
        TotalCoefficientsLuma = _arrays.TotalCoefficientsLuma;
        TotalCoefficientsChroma = _arrays.TotalCoefficientsChroma;
        MotionVectors = _arrays.MotionVectors;
        ReferenceIndexes = _arrays.ReferenceIndexes;
        ReferencePictureIds = _arrays.ReferencePictureIds;
    }

    public VideoFrame Frame { get; }

    public SequenceParameterSet Sequence { get; }

    public RowProgress? Progress { get; init; }

    public bool IsDecoded => Progress is null || Progress.HasReached(0, HeightInMacroblocks * 16);

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

    public int TopPicOrderCount { get; set; }

    public int BottomPicOrderCount { get; set; }

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

    public void Hold() => _holds++;

    public void Release()
    {
        if (--_holds == 0)
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Frame.Dispose();
        _arrayPool?.Return(_arrays);
    }
}
