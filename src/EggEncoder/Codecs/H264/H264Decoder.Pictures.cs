using EggEncoder.Video;

namespace EggEncoder.Codecs.H264;

public sealed partial class H264Decoder
{
    private SliceFilterParameters[] _sliceFilters = [];
    private int _sliceCount;
    private int _nextMacroblock;
    private bool _inOrder;
    private RowProgress? _decoded;
    private Task? _deblocking;
    private readonly Dictionary<int, (PictureParameterSet Pps, SequenceParameterSet Sps, Dequantizer Dequantizer)> _dequantizers = new();
    private readonly DecodedPictureBuffer _buffer = new();
    private readonly CabacDecoder _cabac = new();
    private readonly MacroblockArrayPool _arrayPool = new();
    private CabacNeighbourState? _cabacState;
    private H264Picture? _current;
    private SliceHeader? _currentFirstSlice;

    private void DecodeSlice(H264BitReader reader, int nalUnitType, int referenceIdc, TimeSpan timestamp)
    {
        var header = SliceHeader.Parse(reader, nalUnitType, referenceIdc, _pictureSets, _sequenceSets, out var pps, out var sps);
        if (_current is null)
        {
            StartPicture(header, sps, timestamp);
        }
        else if (_activeSequenceSet != sps || header.FrameNum != _currentFirstSlice!.FrameNum)
        {
            throw new InvalidDataException("H.264: one access unit holds slices of more than one picture");
        }

        var dequantizer = DequantizerFor(pps, sps);
        if (_sliceCount >= _sliceFilters.Length)
        {
            throw new InvalidDataException("H.264: a picture holds more slices than macroblocks");
        }

        var sliceNumber = _sliceCount++;
        _sliceFilters[sliceNumber] = new SliceFilterParameters(header.DisableDeblockingFilterIdc, header.FilterOffsetA, header.FilterOffsetB, pps.ChromaQpIndexOffset, pps.SecondChromaQpIndexOffset);
        _inOrder &= header.FirstMacroblock == _nextMacroblock;
        var referencesL0 = header.Type == SliceType.I ? [] : header.Type == SliceType.P
            ? _buffer.ReferenceListP(_current!, header, sps)
            : _buffer.ReferenceListB(_current!, header, sps, 0);
        var referencesL1 = header.Type == SliceType.B ? _buffer.ReferenceListB(_current!, header, sps, 1) : [];
        if (pps.EntropyCodingCabac && _cabacState?.MacroblockCount != sps.MacroblockCount)
        {
            _cabacState = new CabacNeighbourState(sps.MacroblockCount);
        }

        var slice = new SliceDecoder(_current!, sps, pps, header, sliceNumber, reader, dequantizer, referencesL0, referencesL1, _cabac, _cabacState)
        {
            Progress = _inOrder ? _decoded : null
        };
        slice.Decode();
        if (_inOrder)
        {
            _nextMacroblock = slice.Macroblock;
            _decoded?.Report(0, _nextMacroblock);
        }
    }

    private void StartDeblocking(H264Picture picture, SequenceParameterSet sps)
    {
        _decoded = null;
        _deblocking = null;
        if (Environment.ProcessorCount < 2)
        {
            return;
        }

        var decoded = new RowProgress(1);
        var filters = _sliceFilters;
        var width = sps.WidthInMacroblocks;
        var rows = sps.HeightInMacroblocks;
        var count = sps.MacroblockCount;
        _decoded = decoded;
        _deblocking = Task.Run(() =>
        {
            for (var row = 0; row < rows; row++)
            {
                decoded.WaitFor(0, Math.Min((row + 2) * width, count));
                DeblockingFilter.ApplyRows(picture, filters, row, row + 1);
            }
        });
    }

    private void StopDeblocking()
    {
        if (_deblocking is not { } task)
        {
            return;
        }

        _decoded!.Cancel();
        try
        {
            task.Wait();
        }
        catch (AggregateException)
        {
        }

        _deblocking = null;
        _decoded = null;
    }

    private void FinishDeblocking(H264Picture picture)
    {
        if (_deblocking is not { } task)
        {
            DeblockingFilter.Apply(picture, _sliceFilters);
            return;
        }

        _decoded!.Report(0, picture.WidthInMacroblocks * picture.HeightInMacroblocks);
        try
        {
            task.GetAwaiter().GetResult();
        }
        finally
        {
            _deblocking = null;
            _decoded = null;
        }
    }

    private void StartPicture(SliceHeader header, SequenceParameterSet sps, TimeSpan timestamp)
    {
        if (_activeSequenceSet is { } previous && (previous.WidthInMacroblocks != sps.WidthInMacroblocks || previous.HeightInMacroblocks != sps.HeightInMacroblocks) && !header.IsIdr)
        {
            throw new InvalidDataException("H.264: the picture size changes without an IDR picture");
        }

        _activeSequenceSet = sps;
        _current = new H264Picture(_pool, sps, _arrayPool);
        _current.PictureId = _buffer.NewPictureId();
        _current.ResetMacroblocks();
        _current.Timestamp = timestamp;
        _current.IsIdr = header.IsIdr;
        _current.FrameNum = header.FrameNum;
        _current.IsReference = header.NalReferenceIdc != 0;
        _currentFirstSlice = header;
        if (_sliceFilters.Length != sps.MacroblockCount)
        {
            _sliceFilters = new SliceFilterParameters[sps.MacroblockCount];
        }

        _sliceCount = 0;
        _nextMacroblock = 0;
        _inOrder = true;
        StartDeblocking(_current, sps);
        _buffer.ComputePicOrderCount(_current, header, sps);
    }

    private Dequantizer DequantizerFor(PictureParameterSet pps, SequenceParameterSet sps)
    {
        if (_dequantizers.TryGetValue(pps.Id, out var cached) && cached.Pps == pps && cached.Sps == sps)
        {
            return cached.Dequantizer;
        }

        var dequantizer = new Dequantizer(pps.ScalingListsFor(sps));
        _dequantizers[pps.Id] = (pps, sps, dequantizer);
        return dequantizer;
    }

    private void FinishPicture(Action<VideoFrame> output)
    {
        var picture = _current;
        if (picture is null)
        {
            return;
        }

        _current = null;
        var sps = _activeSequenceSet!;
        var header = _currentFirstSlice!;
        try
        {
            if (Array.IndexOf(picture.Kinds, MacroblockKind.NotDecoded) >= 0)
            {
                throw new InvalidDataException("H.264: the slices of a picture don't cover every macroblock");
            }

            FinishDeblocking(picture);
            _buffer.Mark(picture, header, sps);
        }
        catch
        {
            StopDeblocking();
            picture.Dispose();
            throw;
        }

        Emit(_buffer.Store(picture, header, sps), output);
    }

    private void Emit(IEnumerable<H264Picture> pictures, Action<VideoFrame> output)
    {
        try
        {
            foreach (var picture in pictures)
            {
                output(CopyForOutput(picture, _activeSequenceSet!));
            }
        }
        finally
        {
            foreach (var released in _buffer.TakeReleased())
            {
                released.Dispose();
            }
        }
    }

    private VideoFrame CopyForOutput(H264Picture picture, SequenceParameterSet sps)
    {
        var frame = _pool.Rent(sps.Width, sps.Height, sps.Range, sps.ColorMatrix);
        var source = picture.Frame;
        var left = 2 * sps.CropLeft;
        var top = 2 * sps.CropTop;
        for (var y = 0; y < sps.Height; y++)
        {
            source.GetRow(VideoPlane.Y, top + y).Slice(left, sps.Width).CopyTo(frame.GetRow(VideoPlane.Y, y));
        }

        for (var y = 0; y < frame.ChromaHeight; y++)
        {
            source.GetRow(VideoPlane.U, (top / 2) + y).Slice(left / 2, frame.ChromaWidth).CopyTo(frame.GetRow(VideoPlane.U, y));
            source.GetRow(VideoPlane.V, (top / 2) + y).Slice(left / 2, frame.ChromaWidth).CopyTo(frame.GetRow(VideoPlane.V, y));
        }

        frame.Timestamp = picture.Timestamp;
        return frame;
    }

    private void AbandonPicture()
    {
        StopDeblocking();
        _current?.Dispose();
        _current = null;
    }

    private void FlushPictures(Action<VideoFrame> output) => Emit(_buffer.Flush(), output);

    private void ReleasePictures()
    {
        _buffer.Clear();
        foreach (var released in _buffer.TakeReleased())
        {
            released.Dispose();
        }
    }
}
