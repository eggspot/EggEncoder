namespace EggEncoder.Codecs.H264;

internal sealed partial class SliceDecoder
{
    public MacroblockSyntax Syntax => _syntax;

    public int Macroblock => _macroblock;

    public void StartMacroblock(int address)
    {
        _macroblock = address;
        BeginMacroblock();
    }

    public void ReconstructCurrentMacroblock() => ReconstructMacroblock();

    public void ReconstructSkippedMacroblock() => DecodeSkippedMacroblock();

    public (int X, int Y) PredictedMotion16x16()
    {
        _syntax.Shape = InterShape.Partition16x16;
        var partition = new InterPartition { X = 0, Y = 0, Width = 4, Height = 4, ReferenceL0 = 0, ReferenceL1 = -1, UsesL0 = true };
        return PredictMotion(ref partition, 0);
    }

    public (int X, int Y) PredictedSkipMotion()
    {
        _syntax.Shape = InterShape.Partition16x16;
        _syntax.PartitionCount = 0;
        AddPartition(0, 0, 4, 4, 0);
        _syntax.Partitions[0].ReferenceL0 = 0;
        _syntax.Partitions[0].UsesL0 = true;
        return PredictSkipMotion();
    }

    public int PredictedIntra4x4Mode(int bx, int by) => PredictedIntraMode(bx, by, _picture.IntraModes.AsSpan(_macroblock * 16, 16));

    public (bool Left, bool Top, bool TopLeft, bool TopRight) IntraBlockNeighbours(int bx, int by, int block) =>
        (BlockSamplesAvailable(bx - 1, by, block), BlockSamplesAvailable(bx, by - 1, block), BlockSamplesAvailable(bx - 1, by - 1, block), BlockSamplesAvailable(bx + 1, by - 1, block));

    public (bool Left, bool Top, bool TopLeft) IntraMacroblockNeighbours() =>
        (IsAvailableForIntra(NeighbourMacroblock(-1, 0)), IsAvailableForIntra(NeighbourMacroblock(0, -1)), IsAvailableForIntra(NeighbourMacroblock(-1, -1)));

    public int LumaCoefficientPrediction(int bx, int by) => LumaNc(bx, by);

    public int ChromaCoefficientPrediction(int plane, int cx, int cy) => ChromaNc(plane, cx, cy);
}
