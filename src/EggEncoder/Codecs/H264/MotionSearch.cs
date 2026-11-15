using System.Runtime.CompilerServices;

namespace EggEncoder.Codecs.H264;

[SkipLocalsInit]
internal static class MotionSearch
{
    private const int Margin = 28;
    private const int MaximumSteps = 32;

    private static readonly (int X, int Y)[] Hexagon = [(-2, 0), (-1, -2), (1, -2), (2, 0), (1, 2), (-1, 2)];
    private static readonly (int X, int Y)[] Square = [(-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1)];

    public static (int MvX, int MvY, int Cost) Search(MacroblockEncoder encoder, int predictedX, int predictedY)
    {
        Span<byte> prediction = stackalloc byte[256];
        var search = new Context(encoder, predictedX, predictedY, prediction);
        var bestX = Math.Clamp((predictedX + 2) >> 2, search.MinX, search.MaxX);
        var bestY = Math.Clamp((predictedY + 2) >> 2, search.MinY, search.MaxY);
        var bestCost = search.IntegerCost(bestX, bestY);
        Span<(int X, int Y)> candidates = stackalloc (int, int)[3];
        candidates[0] = (0, 0);
        candidates[1] = Neighbour(encoder, -1, 0);
        candidates[2] = Neighbour(encoder, 0, -1);
        foreach (var (cx, cy) in candidates)
        {
            var x = Math.Clamp((cx + 2) >> 2, search.MinX, search.MaxX);
            var y = Math.Clamp((cy + 2) >> 2, search.MinY, search.MaxY);
            var cost = search.IntegerCost(x, y);
            if (cost < bestCost)
            {
                (bestX, bestY, bestCost) = (x, y, cost);
            }
        }

        for (var step = 0; step < MaximumSteps; step++)
        {
            var moved = false;
            var centerX = bestX;
            var centerY = bestY;
            foreach (var (dx, dy) in Hexagon)
            {
                var x = centerX + dx;
                var y = centerY + dy;
                if (!search.InRange(x, y))
                {
                    continue;
                }

                var cost = search.IntegerCost(x, y);
                if (cost < bestCost)
                {
                    (bestX, bestY, bestCost) = (x, y, cost);
                    moved = true;
                }
            }

            if (!moved)
            {
                break;
            }
        }

        var squareX = bestX;
        var squareY = bestY;
        foreach (var (dx, dy) in Square)
        {
            var x = squareX + dx;
            var y = squareY + dy;
            if (!search.InRange(x, y))
            {
                continue;
            }

            var cost = search.IntegerCost(x, y);
            if (cost < bestCost)
            {
                (bestX, bestY, bestCost) = (x, y, cost);
            }
        }

        var bestMvX = bestX * 4;
        var bestMvY = bestY * 4;
        var bestSubpel = search.SubpelCost(bestMvX, bestMvY);
        foreach (var scale in (ReadOnlySpan<int>)[2, 1])
        {
            var centerX = bestMvX;
            var centerY = bestMvY;
            foreach (var (dx, dy) in Square)
            {
                var mvX = centerX + (dx * scale);
                var mvY = centerY + (dy * scale);
                if (!search.InRange(mvX >> 2, mvY >> 2) || !search.InRange((mvX + 3) >> 2, (mvY + 3) >> 2))
                {
                    continue;
                }

                var cost = search.SubpelCost(mvX, mvY);
                if (cost < bestSubpel)
                {
                    (bestMvX, bestMvY, bestSubpel) = (mvX, mvY, cost);
                }
            }
        }

        return (bestMvX, bestMvY, bestSubpel + encoder.Lambda);
    }

    public static int MotionVectorBits(int dx, int dy) => ExpGolombWriter.SeLength(dx) + ExpGolombWriter.SeLength(dy);

    private static (int X, int Y) Neighbour(MacroblockEncoder encoder, int dx, int dy)
    {
        var x = encoder.MacroblockX + dx;
        var y = encoder.MacroblockY + dy;
        var picture = encoder.Picture;
        if (x < 0 || y < 0)
        {
            return (0, 0);
        }

        var macroblock = (y * picture.WidthInMacroblocks) + x;
        if (picture.IsIntra(macroblock) || picture.ReferenceIndexes[0][macroblock * 4] < 0)
        {
            return (0, 0);
        }

        var vectors = picture.MotionVectors[0];
        return (vectors[macroblock * 32], vectors[(macroblock * 32) + 1]);
    }

    private readonly ref struct Context
    {
        private readonly H264Picture _reference;
        private readonly ReadOnlySpan<byte> _source;
        private readonly Span<byte> _prediction;
        private readonly byte[] _buffer;
        private readonly int _sourceStride;
        private readonly int _stride;
        private readonly int _origin;
        private readonly int _baseX;
        private readonly int _baseY;
        private readonly int _lambda;
        private readonly int _predictedX;
        private readonly int _predictedY;

        public Context(MacroblockEncoder encoder, int predictedX, int predictedY, Span<byte> prediction)
        {
            _reference = encoder.Reference;
            var frame = _reference.Frame;
            _source = encoder.SourceLuma;
            _sourceStride = encoder.SourceStride;
            _prediction = prediction;
            _buffer = frame.Buffer;
            _stride = frame.StrideY;
            _baseX = encoder.MacroblockX * 16;
            _baseY = encoder.MacroblockY * 16;
            _origin = frame.OffsetY + (_baseY * _stride) + _baseX;
            _lambda = encoder.Lambda;
            _predictedX = predictedX;
            _predictedY = predictedY;
            MinX = -Margin - _baseX;
            MaxX = frame.Width - 16 + Margin - _baseX;
            MinY = -Margin - _baseY;
            MaxY = frame.Height - 16 + Margin - _baseY;
        }

        public int MinX { get; }

        public int MaxX { get; }

        public int MinY { get; }

        public int MaxY { get; }

        public bool InRange(int x, int y) => x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;

        public int IntegerCost(int x, int y) =>
            BlockCost.Sad16x16(_source, _sourceStride, _buffer.AsSpan(_origin + (y * _stride) + x), _stride)
            + (_lambda * MotionVectorBits((x * 4) - _predictedX, (y * 4) - _predictedY));

        public int SubpelCost(int mvX, int mvY)
        {
            InterPrediction.Luma(_reference, _baseX + (mvX >> 2), _baseY + (mvY >> 2), mvX & 3, mvY & 3, 16, 16, _prediction, 16);
            return BlockCost.Satd(_source, _sourceStride, _prediction, 16, 16, 16) + (_lambda * MotionVectorBits(mvX - _predictedX, mvY - _predictedY));
        }
    }
}
