using System.Runtime.CompilerServices;
using EggEncoder.Transform;
using EggEncoder.Video;

namespace EggEncoder.Codecs.H264;

[SkipLocalsInit]
internal sealed partial class MacroblockEncoder
{
    private static readonly int[] IntraCodeNumbers = Invert(MacroblockLayout.IntraCodedBlockPattern);
    private static readonly int[] InterCodeNumbers = Invert(MacroblockLayout.InterCodedBlockPattern);
    private static readonly byte[] DecimationScores = [3, 2, 2, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    private readonly SliceDecoder _slice;
    private readonly H264Picture _picture;
    private readonly VideoFrame _source;
    private readonly H264Picture? _reference;
    private readonly int _qp;
    private readonly int _chromaQp;
    private readonly int _lambda;
    private readonly Dequantizer _dequantizer;
    private readonly byte[] _recon;
    private readonly int _strideY;
    private readonly int _strideUV;
    private readonly int[] _residual = new int[64];
    private readonly int[] _coefficients = new int[16];
    private readonly short[] _scanned = new short[16];
    private readonly short[] _raster = new short[16];
    private readonly byte[] _lumaPrediction = new byte[256];
    private readonly byte[] _cbPrediction = new byte[64];
    private readonly byte[] _crPrediction = new byte[64];
    private readonly byte[] _trial = new byte[256];
    private readonly MacroblockSyntax _intra4x4 = new();
    private readonly byte[] _intra4x4Modes = new byte[16];
    private int _mbX;
    private int _mbY;
    private int _lumaOffset;
    private int _offsetU;
    private int _offsetV;

    public MacroblockEncoder(SliceDecoder slice, H264Picture picture, VideoFrame source, H264Picture? reference, int qp, Dequantizer dequantizer, int chromaQpOffset)
    {
        _slice = slice;
        _picture = picture;
        _source = source;
        _reference = reference;
        _qp = qp;
        _chromaQp = Dequantizer.ChromaQp(qp, chromaQpOffset);
        _lambda = Math.Max(1, (int)Math.Round(0.92 * Math.Pow(2, (qp - 12) / 6.0)));
        _dequantizer = dequantizer;
        var frame = picture.Frame;
        _recon = frame.Buffer;
        _strideY = frame.StrideY;
        _strideUV = frame.StrideUV;
    }

    private Span<byte> SourceY => _source.Buffer.AsSpan(_source.OffsetY + (_mbY * 16 * _source.StrideY) + (_mbX * 16));

    private Span<byte> SourceU => _source.Buffer.AsSpan(_source.OffsetU + (_mbY * 8 * _source.StrideUV) + (_mbX * 8));

    private Span<byte> SourceV => _source.Buffer.AsSpan(_source.OffsetV + (_mbY * 8 * _source.StrideUV) + (_mbX * 8));

    public void EncodeSlice(BitWriter bits, CabacEncoder? cabac, SliceType type)
    {
        var width = _picture.WidthInMacroblocks;
        var count = width * _picture.HeightInMacroblocks;
        var skipRun = 0;
        var frame = _picture.Frame;
        if (cabac is not null)
        {
            while (bits.BitPosition % 8 != 0)
            {
                bits.WriteBits(1, 1);
            }

            cabac.InitializeContexts(type, 0, _qp);
            cabac.Start(bits);
            _slice.StartCabacWriting();
        }

        for (var macroblock = 0; macroblock < count; macroblock++)
        {
            _mbX = macroblock % width;
            _mbY = macroblock / width;
            _lumaOffset = frame.OffsetY + (_mbY * 16 * _strideY) + (_mbX * 16);
            _offsetU = frame.OffsetU + (_mbY * 8 * _strideUV) + (_mbX * 8);
            _offsetV = frame.OffsetV + (_mbY * 8 * _strideUV) + (_mbX * 8);
            _slice.StartMacroblock(macroblock);
            if (_reference is not null && TryEncodeSkip())
            {
                _slice.ReconstructSkippedMacroblock();
                if (cabac is not null)
                {
                    _slice.WriteSkippedMacroblockCabac(cabac);
                    cabac.EncodeTerminate(macroblock == count - 1 ? 1 : 0);
                }
                else
                {
                    skipRun++;
                }

                continue;
            }

            var syntax = _slice.Syntax;
            if (_reference is not null && cabac is null)
            {
                bits.WriteUe((uint)skipRun);
                skipRun = 0;
            }

            var (mvX, mvY) = Decide();
            _slice.ReconstructCurrentMacroblock();
            if (cabac is not null)
            {
                _slice.WriteMacroblockCabac(cabac);
                cabac.EncodeTerminate(macroblock == count - 1 ? 1 : 0);
            }
            else
            {
                WriteMacroblock(bits, syntax, mvX, mvY);
            }
        }

        if (cabac is not null)
        {
            bits.ByteAlign();
            return;
        }

        if (skipRun > 0)
        {
            bits.WriteUe((uint)skipRun);
        }

        bits.WriteTrailingBits();
    }

    private (int MvdX, int MvdY) Decide()
    {
        var syntax = _slice.Syntax;
        if (_reference is null)
        {
            var intra16Cost = AnalyseIntra16x16(out var intra16Mode);
            var intra4x4Cost = AnalyseIntra4x4();
            if (intra4x4Cost < intra16Cost)
            {
                UseIntra4x4();
            }
            else
            {
                EncodeIntra16x16(intra16Mode);
            }

            EncodeIntraChroma();
            return (0, 0);
        }

        var predicted = _slice.PredictedMotion16x16();
        var (mvX, mvY, interCost) = MotionSearch.Search(this, predicted.X, predicted.Y);
        var intraCost = AnalyseIntra16x16(out var mode);
        if (intraCost < interCost)
        {
            var intra4x4Cost = AnalyseIntra4x4();
            if (intra4x4Cost < intraCost)
            {
                UseIntra4x4();
            }
            else
            {
                EncodeIntra16x16(mode);
            }

            EncodeIntraChroma();
            return (0, 0);
        }

        EncodeInter(mvX, mvY, predicted.X, predicted.Y);
        return (mvX - predicted.X, mvY - predicted.Y);
    }

    private bool TryEncodeSkip()
    {
        var (mvX, mvY) = _slice.PredictedSkipMotion();
        PredictInter(mvX, mvY);
        var source = SourceY;
        for (var block = 0; block < 16; block++)
        {
            var bx = MacroblockLayout.BlockX[block] * 4;
            var by = MacroblockLayout.BlockY[block] * 4;
            ResidualBlock(source[((by * _source.StrideY) + bx)..], _source.StrideY, _lumaPrediction.AsSpan((by * 16) + bx), 16);
            ForwardTransform.Core4x4(_residual.AsSpan(0, 16), _coefficients);
            if (Quantizer.Quantize4x4(_coefficients, _raster, _scanned, _qp, intra: false, 0) != 0 && DecimationScore(_scanned) > 2)
            {
                return false;
            }
        }

        return ChromaQuantizesToZero(SourceU, _cbPrediction) && ChromaQuantizesToZero(SourceV, _crPrediction);
    }

    private bool ChromaQuantizesToZero(Span<byte> source, byte[] prediction)
    {
        Span<int> dc = stackalloc int[4];
        for (var block = 0; block < 4; block++)
        {
            var bx = (block & 1) * 4;
            var by = (block >> 1) * 4;
            ResidualBlock(source[((by * _source.StrideUV) + bx)..], _source.StrideUV, prediction.AsSpan((by * 8) + bx), 8);
            ForwardTransform.Core4x4(_residual.AsSpan(0, 16), _coefficients);
            dc[block] = _coefficients[0];
            if (Quantizer.Quantize4x4(_coefficients, _raster, _scanned, _chromaQp, intra: false, 1) != 0)
            {
                return false;
            }
        }

        ForwardTransform.Hadamard2x2(dc);
        Span<short> levels = stackalloc short[4];
        return Quantizer.QuantizeDc(dc, levels, _chromaQp, intra: false) == 0;
    }

    public void PredictInter(int mvX, int mvY)
    {
        var x = (_mbX * 16) + (mvX >> 2);
        var y = (_mbY * 16) + (mvY >> 2);
        InterPrediction.Luma(_reference!, x, y, mvX & 3, mvY & 3, 16, 16, _lumaPrediction, 16);
        var cx = (_mbX * 8) + (mvX >> 3);
        var cy = (_mbY * 8) + (mvY >> 3);
        InterPrediction.Chroma(_reference!, false, cx, cy, mvX & 7, mvY & 7, 8, 8, _cbPrediction, 8);
        InterPrediction.Chroma(_reference!, true, cx, cy, mvX & 7, mvY & 7, 8, 8, _crPrediction, 8);
    }

    private void ResidualBlock(ReadOnlySpan<byte> source, int sourceStride, ReadOnlySpan<byte> prediction, int predictionStride)
    {
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                _residual[(y * 4) + x] = source[(y * sourceStride) + x] - prediction[(y * predictionStride) + x];
            }
        }
    }

    private static int DecimationScore(ReadOnlySpan<short> scanned)
    {
        var index = scanned.Length - 1;
        while (index >= 0 && scanned[index] == 0)
        {
            index--;
        }

        var score = 0;
        while (index >= 0)
        {
            if (Math.Abs(scanned[index--]) > 1)
            {
                return 9;
            }

            var run = 0;
            while (index >= 0 && scanned[index] == 0)
            {
                index--;
                run++;
            }

            score += DecimationScores[run];
        }

        return score;
    }

    private static int[] Invert(int[] table)
    {
        var inverse = new int[table.Length];
        for (var code = 0; code < table.Length; code++)
        {
            inverse[table[code]] = code;
        }

        return inverse;
    }

    public int Lambda => _lambda;

    public int MacroblockX => _mbX;

    public int MacroblockY => _mbY;

    public H264Picture Reference => _reference!;

    public H264Picture Picture => _picture;

    public ReadOnlySpan<byte> SourceLuma => SourceY;

    public int SourceStride => _source.StrideY;
}
