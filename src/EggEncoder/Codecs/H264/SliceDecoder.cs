using System.Runtime.CompilerServices;

namespace EggEncoder.Codecs.H264;

[SkipLocalsInit]
internal sealed partial class SliceDecoder
{
    private readonly H264Picture _picture;
    private readonly SequenceParameterSet _sps;
    private readonly PictureParameterSet _pps;
    private readonly SliceHeader _header;
    private readonly int _sliceNumber;
    private readonly H264BitReader _reader;
    private readonly Dequantizer _dequantizer;
    private MacroblockSyntax _syntax = new();
    private readonly int _width;
    private readonly int[] _dequantized = new int[64];
    private readonly H264Picture[] _referencesL0;
    private readonly H264Picture[] _referencesL1;
    private readonly bool[] _motionAssigned = new bool[16];
    private readonly CabacDecoder? _cabac;
    private readonly CabacNeighbourState? _cabacState;
    private int _qp;
    private int _macroblock;
    private int _mbX;
    private int _mbY;

    public RowProgress? Progress { get; init; }

    public SliceDecoder(H264Picture picture, SequenceParameterSet sps, PictureParameterSet pps, SliceHeader header, int sliceNumber, H264BitReader reader, Dequantizer dequantizer, H264Picture[] referencesL0, H264Picture[] referencesL1, CabacDecoder? cabac = null, CabacNeighbourState? cabacState = null)
    {
        _cabac = cabac;
        _cabacState = cabacState;
        _referencesL0 = referencesL0;
        _referencesL1 = referencesL1;
        _picture = picture;
        _sps = sps;
        _pps = pps;
        _header = header;
        _sliceNumber = sliceNumber;
        _reader = reader;
        _dequantizer = dequantizer;
        _width = sps.WidthInMacroblocks;
        _qp = pps.InitialQp + header.QpDelta;
    }

    public void Decode()
    {
        if (_pps.EntropyCodingCabac)
        {
            DecodeCabac();
            return;
        }

        _macroblock = _header.FirstMacroblock;
        var moreData = true;
        while (moreData)
        {
            if (_header.Type != SliceType.I)
            {
                var skipRun = _reader.ReadUe((uint)_sps.MacroblockCount, "mb_skip_run");
                for (var i = 0; i < skipRun; i++)
                {
                    BeginMacroblock();
                    if (_header.Type == SliceType.B)
                    {
                        DecodeSkippedMacroblockB();
                    }
                    else
                    {
                        DecodeSkippedMacroblock();
                    }

                    _macroblock++;
                }

                if (skipRun > 0 && !_reader.MoreRbspData())
                {
                    break;
                }
            }

            BeginMacroblock();
            if (_header.Type == SliceType.I)
            {
                ParseIntraMacroblock((int)_reader.ReadUe(25, "mb_type"));
            }
            else if (_header.Type == SliceType.P)
            {
                var mbType = (int)_reader.ReadUe(30, "mb_type");
                if (mbType < 5)
                {
                    ParseInterMacroblockP(mbType);
                }
                else
                {
                    ParseIntraMacroblock(mbType - 5);
                }
            }
            else
            {
                var mbType = (int)_reader.ReadUe(48, "mb_type");
                if (mbType < 23)
                {
                    ParseInterMacroblockB(mbType);
                }
                else
                {
                    ParseIntraMacroblock(mbType - 23);
                }
            }

            ReconstructMacroblock();
            moreData = _reader.MoreRbspData();
            _macroblock++;
        }
    }

    private void BeginMacroblock()
    {
        if (_macroblock >= _sps.MacroblockCount)
        {
            throw new InvalidDataException("H.264: a slice runs past the last macroblock");
        }

        if (_picture.Kinds[_macroblock] != MacroblockKind.NotDecoded)
        {
            throw new InvalidDataException("H.264: two slices cover the same macroblock");
        }

        _mbX = _macroblock % _width;
        if (_mbX == 0)
        {
            Progress?.Report(0, _macroblock);
        }

        _mbY = _macroblock / _width;
        _picture.TotalCoefficientsLuma.AsSpan(_macroblock * 16, 16).Clear();
        _picture.TotalCoefficientsChroma.AsSpan(_macroblock * 8, 8).Clear();
        _picture.SliceNumbers[_macroblock] = -1;
        for (var list = 0; list < 2; list++)
        {
            _picture.ReferenceIndexes[list].AsSpan(_macroblock * 4, 4).Fill(-1);
            _picture.MotionVectors[list].AsSpan(_macroblock * 32, 32).Clear();
        }

        Array.Clear(_motionAssigned);
    }

    private void StartIntraMacroblock(int mbType)
    {
        var syntax = _syntax;
        syntax.ClearResidual();
        syntax.Transform8x8 = false;
        syntax.QpDelta = 0;
        syntax.PartitionCount = 0;
        if (mbType == 25)
        {
            syntax.Kind = MacroblockKind.IntraPcm;
        }
        else if (mbType == 0)
        {
            syntax.Kind = MacroblockKind.IntraNxN;
        }
        else
        {
            syntax.Kind = MacroblockKind.Intra16x16;
            syntax.Intra16x16PredictionMode = (mbType - 1) % 4;
            syntax.CodedBlockPatternChroma = (mbType - 1) / 4 % 3;
            syntax.CodedBlockPatternLuma = mbType >= 13 ? 15 : 0;
        }
    }

    private void ParseIntraMacroblock(int mbType)
    {
        var syntax = _syntax;
        StartIntraMacroblock(mbType);
        if (syntax.Kind == MacroblockKind.IntraPcm)
        {
            _reader.ByteAlign();
            _reader.ReadAlignedBytes(384).CopyTo(syntax.PcmSamples);
            return;
        }

        if (syntax.Kind == MacroblockKind.IntraNxN)
        {
            syntax.Transform8x8 = _pps.Transform8x8Mode && _reader.ReadFlag();
            for (var block = 0; block < (syntax.Transform8x8 ? 4 : 16); block++)
            {
                syntax.PreviousIntraModeFlags[block] = _reader.ReadFlag();
                if (!syntax.PreviousIntraModeFlags[block])
                {
                    syntax.RemainingIntraModes[block] = (byte)_reader.ReadBits(3);
                }
            }

            syntax.IntraChromaPredictionMode = (int)_reader.ReadUe(3, "intra_chroma_pred_mode");
            var pattern = MacroblockLayout.IntraCodedBlockPattern[_reader.ReadUe(47, "coded_block_pattern")];
            syntax.CodedBlockPatternLuma = pattern & 15;
            syntax.CodedBlockPatternChroma = pattern >> 4;
        }
        else
        {
            syntax.IntraChromaPredictionMode = (int)_reader.ReadUe(3, "intra_chroma_pred_mode");
        }

        if (syntax.CodedBlockPatternLuma > 0 || syntax.CodedBlockPatternChroma > 0 || syntax.Kind == MacroblockKind.Intra16x16)
        {
            syntax.QpDelta = _reader.ReadSe(-26, 25, "mb_qp_delta");
            ReadResidual();
        }
    }

    private void ReadResidual()
    {
        var syntax = _syntax;
        var lumaTotals = _picture.TotalCoefficientsLuma.AsSpan(_macroblock * 16, 16);
        var chromaTotals = _picture.TotalCoefficientsChroma.AsSpan(_macroblock * 8, 8);
        Span<short> scanned = stackalloc short[16];
        if (syntax.Transform8x8)
        {
            ReadLumaResidual8x8Cavlc(lumaTotals, scanned);
        }

        if (syntax.Kind == MacroblockKind.Intra16x16)
        {
            scanned.Clear();
            var total = CavlcResidual.ReadBlock(_reader, LumaNc(0, 0), 16, scanned);
            syntax.LumaDcCoded = total > 0;
            scanned.CopyTo(syntax.LumaDc);
        }

        for (var block = 0; block < 16 && !syntax.Transform8x8; block++)
        {
            var bx = MacroblockLayout.BlockX[block];
            var by = MacroblockLayout.BlockY[block];
            if ((syntax.CodedBlockPatternLuma & (1 << (block >> 2))) == 0)
            {
                continue;
            }

            scanned.Clear();
            var ac = syntax.Kind == MacroblockKind.Intra16x16;
            var total = CavlcResidual.ReadBlock(_reader, LumaNc(bx, by), ac ? 15 : 16, scanned);
            lumaTotals[(by * 4) + bx] = (byte)total;
            if (total == 0)
            {
                continue;
            }

            syntax.LumaBlockCoded[block] = true;
            var coefficients = syntax.LumaCoefficients.AsSpan(block * 16, 16);
            for (var i = 0; i < (ac ? 15 : 16); i++)
            {
                coefficients[MacroblockLayout.ZigZag4x4[i + (ac ? 1 : 0)]] = scanned[i];
            }
        }

        if (syntax.CodedBlockPatternChroma == 0)
        {
            return;
        }

        for (var plane = 0; plane < 2; plane++)
        {
            scanned.Clear();
            var total = CavlcResidual.ReadBlock(_reader, -1, 4, scanned);
            syntax.ChromaDcCoded[plane] = total > 0;
            scanned[..4].CopyTo(syntax.ChromaDc.AsSpan(plane * 4, 4));
        }

        if (syntax.CodedBlockPatternChroma < 2)
        {
            return;
        }

        for (var plane = 0; plane < 2; plane++)
        {
            for (var block = 0; block < 4; block++)
            {
                var cx = block & 1;
                var cy = block >> 1;
                scanned.Clear();
                var total = CavlcResidual.ReadBlock(_reader, ChromaNc(plane, cx, cy), 15, scanned);
                chromaTotals[(plane * 4) + block] = (byte)total;
                if (total == 0)
                {
                    continue;
                }

                syntax.ChromaAcBlockCoded[(plane * 4) + block] = true;
                var coefficients = syntax.ChromaAc.AsSpan(((plane * 4) + block) * 16, 16);
                for (var i = 0; i < 15; i++)
                {
                    coefficients[MacroblockLayout.ZigZag4x4[i + 1]] = scanned[i];
                }
            }
        }
    }

    private void ReadLumaResidual8x8Cavlc(Span<byte> lumaTotals, Span<short> scanned)
    {
        var syntax = _syntax;
        Span<short> levels = stackalloc short[64];
        for (var block8 = 0; block8 < 4; block8++)
        {
            if ((syntax.CodedBlockPatternLuma & (1 << block8)) == 0)
            {
                continue;
            }

            levels.Clear();
            var any = false;
            for (var part = 0; part < 4; part++)
            {
                var block = (block8 * 4) + part;
                var bx = MacroblockLayout.BlockX[block];
                var by = MacroblockLayout.BlockY[block];
                scanned.Clear();
                var total = CavlcResidual.ReadBlock(_reader, LumaNc(bx, by), 16, scanned);
                lumaTotals[(by * 4) + bx] = (byte)total;
                any |= total > 0;
                for (var i = 0; i < 16; i++)
                {
                    levels[(4 * i) + part] = scanned[i];
                }
            }

            if (any)
            {
                StoreLuma8x8(block8, levels);
            }
        }
    }

    private void StoreLuma8x8(int block8, ReadOnlySpan<short> levels)
    {
        _syntax.LumaBlock8x8Coded[block8] = true;
        var coefficients = _syntax.LumaCoefficients.AsSpan(block8 * 64, 64);
        for (var i = 0; i < 64; i++)
        {
            coefficients[MacroblockLayout.ZigZag8x8[i]] = levels[i];
        }
    }

    private int LumaNc(int bx, int by)
    {
        var (availableA, totalA) = LumaNeighbourTotal(bx - 1, by);
        var (availableB, totalB) = LumaNeighbourTotal(bx, by - 1);
        return Combine(availableA, totalA, availableB, totalB);
    }

    private int ChromaNc(int plane, int cx, int cy)
    {
        var (availableA, totalA) = ChromaNeighbourTotal(plane, cx - 1, cy);
        var (availableB, totalB) = ChromaNeighbourTotal(plane, cx, cy - 1);
        return Combine(availableA, totalA, availableB, totalB);
    }

    private static int Combine(bool availableA, int totalA, bool availableB, int totalB) =>
        availableA && availableB ? (totalA + totalB + 1) >> 1 : availableA ? totalA : availableB ? totalB : 0;

    private (bool Available, int Total) LumaNeighbourTotal(int bx, int by)
    {
        var (macroblock, x, y) = Locate(bx, by, 4);
        if (macroblock < 0)
        {
            return (false, 0);
        }

        return (true, _picture.TotalCoefficientsLuma[(macroblock * 16) + (y * 4) + x]);
    }

    private (bool Available, int Total) ChromaNeighbourTotal(int plane, int cx, int cy)
    {
        var (macroblock, x, y) = Locate(cx, cy, 2);
        if (macroblock < 0)
        {
            return (false, 0);
        }

        return (true, _picture.TotalCoefficientsChroma[(macroblock * 8) + (plane * 4) + (y * 2) + x]);
    }

    private (int Macroblock, int X, int Y) Locate(int x, int y, int blocksPerSide)
    {
        if (x >= 0 && y >= 0 && x < blocksPerSide)
        {
            return (_macroblock, x, y);
        }

        var dx = x < 0 ? -1 : x >= blocksPerSide ? 1 : 0;
        var dy = y < 0 ? -1 : 0;
        var neighbour = NeighbourMacroblock(dx, dy);
        return neighbour < 0 ? (-1, 0, 0) : (neighbour, (x + blocksPerSide) % blocksPerSide, (y + blocksPerSide) % blocksPerSide);
    }

    private int NeighbourMacroblock(int dx, int dy)
    {
        var x = _mbX + dx;
        var y = _mbY + dy;
        if (x < 0 || y < 0 || x >= _width)
        {
            return -1;
        }

        var address = (y * _width) + x;
        if (address >= _macroblock && !(dx == 0 && dy == 0))
        {
            return -1;
        }

        return _picture.SliceNumbers[address] == _sliceNumber ? address : -1;
    }

    private bool IsAvailableForIntra(int macroblock) =>
        macroblock >= 0 && (!_pps.ConstrainedIntraPrediction || _picture.IsIntra(macroblock));
}
