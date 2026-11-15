namespace EggEncoder.Codecs.H264;

internal sealed class CabacNeighbourState
{
    public const int LumaDcFlag = 1 << 16;

    public const int AllBlocksCoded = -1;

    public CabacNeighbourState(int macroblockCount)
    {
        MacroblockCount = macroblockCount;
        CodedBlockFlags = new int[macroblockCount];
        CodedBlockPatterns = new byte[macroblockCount];
        IntraChromaModes = new byte[macroblockCount];
        AbsoluteMvd = [new byte[macroblockCount * 32], new byte[macroblockCount * 32]];
    }

    public int MacroblockCount { get; }

    public int[] CodedBlockFlags { get; }

    public byte[] CodedBlockPatterns { get; }

    public byte[] IntraChromaModes { get; }

    public byte[][] AbsoluteMvd { get; }

    public static int ChromaDcFlag(int plane) => 1 << (17 + plane);

    public static int ChromaAcFlag(int plane, int x, int y) => 1 << (19 + (plane * 4) + (y * 2) + x);

    public void Record(int macroblock, int codedBlockFlags, int codedBlockPattern, int intraChromaMode)
    {
        CodedBlockFlags[macroblock] = codedBlockFlags;
        CodedBlockPatterns[macroblock] = (byte)codedBlockPattern;
        IntraChromaModes[macroblock] = (byte)intraChromaMode;
    }

    public void ClearMotion(int macroblock)
    {
        AbsoluteMvd[0].AsSpan(macroblock * 32, 32).Clear();
        AbsoluteMvd[1].AsSpan(macroblock * 32, 32).Clear();
    }
}
