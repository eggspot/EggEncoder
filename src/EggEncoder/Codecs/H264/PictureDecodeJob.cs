namespace EggEncoder.Codecs.H264;

internal sealed class SliceEntropyContext
{
    private CabacNeighbourState? _state;

    public CabacDecoder Cabac { get; } = new();

    public CabacNeighbourState StateFor(int macroblockCount)
    {
        if (_state?.MacroblockCount != macroblockCount)
        {
            _state = new CabacNeighbourState(macroblockCount);
        }

        return _state;
    }
}

internal sealed class PictureDecodeJob
{
    private readonly List<SliceDecoder> _slices = new();
    private readonly List<SliceFilterParameters> _filters = new();
    private readonly List<H264Picture> _references = new();
    private bool _progressive = true;
    private bool _finishedInline;
    private bool _deblockWhileDecoding;
    private Task? _task;

    public PictureDecodeJob(H264Picture picture, SliceEntropyContext context)
    {
        Picture = picture;
        Context = context;
    }

    public H264Picture Picture { get; }

    public SliceEntropyContext Context { get; }

    public Exception? Error { get; private set; }

    public bool IsCompleted => _task?.IsCompleted ?? _finishedInline;

    public void AddSlice(SequenceParameterSet sps, PictureParameterSet pps, SliceHeader header, H264BitReader reader, Dequantizer dequantizer, H264Picture[] referencesL0, H264Picture[] referencesL1)
    {
        if (_slices.Count >= sps.MacroblockCount)
        {
            throw new InvalidDataException("H.264: a picture holds more slices than macroblocks");
        }

        _progressive &= _slices.Count == 0 ? header.FirstMacroblock == 0 : header.FirstMacroblock > _slices[^1].FirstMacroblock;
        _filters.Add(new SliceFilterParameters(header.DisableDeblockingFilterIdc, header.FilterOffsetA, header.FilterOffsetB, pps.ChromaQpIndexOffset, pps.SecondChromaQpIndexOffset));
        var cabacState = pps.EntropyCodingCabac ? Context.StateFor(sps.MacroblockCount) : null;
        _slices.Add(new SliceDecoder(Picture, sps, pps, header, _slices.Count, reader, dequantizer, referencesL0, referencesL1, Context.Cabac, cabacState));
        KeepReferences(referencesL0);
        KeepReferences(referencesL1);
    }

    public void Start(bool onCallingThread)
    {
        Picture.Hold();
        foreach (var reference in _references)
        {
            reference.Hold();
        }

        _deblockWhileDecoding = !onCallingThread;
        if (onCallingThread)
        {
            Execute();
            _finishedInline = true;
            return;
        }

        _task = Task.Factory.StartNew(Execute, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void Wait() => _task?.GetAwaiter().GetResult();

    public void Cancel() => Picture.Progress?.Cancel();

    public void ReleaseHolds()
    {
        foreach (var reference in _references)
        {
            reference.Release();
        }

        Picture.Release();
    }

    private void KeepReferences(H264Picture[] references)
    {
        foreach (var reference in references)
        {
            if (reference is not null && !_references.Contains(reference))
            {
                _references.Add(reference);
            }
        }
    }

    private void Execute()
    {
        try
        {
            Decode();
        }
        catch (Exception error)
        {
            Error = error;
            Picture.Progress?.Cancel();
        }
    }

    private void Decode()
    {
        if (_progressive && _deblockWhileDecoding)
        {
            DecodeAndDeblockRowByRow();
            return;
        }

        var picture = Picture;
        var count = picture.WidthInMacroblocks * picture.HeightInMacroblocks;
        var decoded = _progressive && Environment.ProcessorCount > 1 ? new RowProgress(1, spinRounds: 0) : null;
        var deblocking = decoded is null ? null : StartDeblocking(decoded);
        try
        {
            Action<int>? reporting = decoded is null ? null : macroblocks => decoded.Report(0, macroblocks);
            var next = 0;
            foreach (var slice in _slices)
            {
                if (slice.FirstMacroblock != next)
                {
                    reporting = null;
                }

                slice.RowStarted = reporting;
                slice.Decode();
                if (reporting is not null)
                {
                    next = slice.Macroblock;
                    reporting(next);
                }
            }

            if (Array.IndexOf(picture.Kinds, MacroblockKind.NotDecoded) >= 0)
            {
                throw new InvalidDataException("H.264: the slices of a picture don't cover every macroblock");
            }

            if (deblocking is null)
            {
                DeblockingFilter.Apply(picture, _filters);
            }
            else
            {
                decoded!.Report(0, count);
                deblocking.GetAwaiter().GetResult();
            }

            picture.Progress?.Report(0, picture.HeightInMacroblocks * 16);
        }
        catch
        {
            if (deblocking is not null)
            {
                decoded!.Cancel();
                try
                {
                    deblocking.Wait();
                }
                catch (AggregateException)
                {
                }
            }

            throw;
        }
    }

    private void DecodeAndDeblockRowByRow()
    {
        var picture = Picture;
        var width = picture.WidthInMacroblocks;
        var rows = picture.HeightInMacroblocks;
        var filters = _filters;
        var masks = System.Buffers.ArrayPool<int>.Shared.Rent(width * rows);
        var deblocked = 0;
        void DeblockUpTo(int endRow)
        {
            for (; deblocked < endRow; deblocked++)
            {
                DeblockingFilter.ComputeEdgeMasks(picture, masks, deblocked, deblocked + 1);
                DeblockingFilter.FilterRows(picture, filters, masks, deblocked, deblocked + 1);
                picture.Progress?.Report(0, (16 * deblocked) + 13);
            }
        }

        try
        {
            Action<int>? rowStarted = macroblocks => DeblockUpTo(Math.Max(0, (macroblocks / width) - 1));
            var next = 0;
            foreach (var slice in _slices)
            {
                if (slice.FirstMacroblock != next)
                {
                    rowStarted = null;
                }

                slice.RowStarted = rowStarted;
                slice.Decode();
                if (rowStarted is not null)
                {
                    next = slice.Macroblock;
                }
            }

            if (Array.IndexOf(picture.Kinds, MacroblockKind.NotDecoded) >= 0)
            {
                throw new InvalidDataException("H.264: the slices of a picture don't cover every macroblock");
            }

            DeblockUpTo(rows);
            picture.Progress?.Report(0, rows * 16);
        }
        finally
        {
            System.Buffers.ArrayPool<int>.Shared.Return(masks);
        }
    }

    private Task StartDeblocking(RowProgress decoded)
    {
        var picture = Picture;
        var filters = _filters;
        var width = picture.WidthInMacroblocks;
        var rows = picture.HeightInMacroblocks;
        var count = width * rows;
        return Task.Factory.StartNew(
            () =>
            {
                var masks = System.Buffers.ArrayPool<int>.Shared.Rent(count);
                try
                {
                    for (var row = 0; row < rows; row++)
                    {
                        decoded.WaitFor(0, Math.Min((row + 2) * width, count));
                        DeblockingFilter.ComputeEdgeMasks(picture, masks, row, row + 1);
                        DeblockingFilter.FilterRows(picture, filters, masks, row, row + 1);
                        picture.Progress?.Report(0, (16 * row) + 13);
                    }
                }
                finally
                {
                    System.Buffers.ArrayPool<int>.Shared.Return(masks);
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }
}
