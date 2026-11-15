using System.Runtime.ExceptionServices;
using EggEncoder.Video;

namespace EggEncoder.Codecs.H264;

public sealed partial class H264Decoder
{
    private const int MaxDefaultParallelism = 8;

    private readonly Dictionary<int, (PictureParameterSet Pps, SequenceParameterSet Sps, Dequantizer Dequantizer)> _dequantizers = new();
    private readonly DecodedPictureBuffer _buffer = new();
    private readonly MacroblockArrayPool _arrayPool = new();
    private readonly Stack<SliceEntropyContext> _entropyContexts = new();
    private readonly Queue<PictureDecodeJob> _jobs = new();
    private readonly Queue<H264Picture> _pendingOutput = new();
    private PictureDecodeJob? _current;
    private SliceHeader? _currentFirstSlice;

    private static int ResolveParallelism(int maxDegreeOfParallelism) =>
        maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : Math.Clamp(Environment.ProcessorCount, 1, MaxDefaultParallelism);

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

        var job = _current!;
        var dequantizer = DequantizerFor(pps, sps);
        var referencesL0 = header.Type == SliceType.I ? [] : header.Type == SliceType.P
            ? _buffer.ReferenceListP(job.Picture, header, sps)
            : _buffer.ReferenceListB(job.Picture, header, sps, 0);
        var referencesL1 = header.Type == SliceType.B ? _buffer.ReferenceListB(job.Picture, header, sps, 1) : [];
        job.AddSlice(sps, pps, header, reader, dequantizer, referencesL0, referencesL1);
    }

    private void StartPicture(SliceHeader header, SequenceParameterSet sps, TimeSpan timestamp)
    {
        if (_activeSequenceSet is { } previous && (previous.WidthInMacroblocks != sps.WidthInMacroblocks || previous.HeightInMacroblocks != sps.HeightInMacroblocks) && !header.IsIdr)
        {
            throw new InvalidDataException("H.264: the picture size changes without an IDR picture");
        }

        _activeSequenceSet = sps;
        var picture = new H264Picture(_pool, sps, _arrayPool) { Progress = new RowProgress(1, spinRounds: 0) };
        picture.PictureId = _buffer.NewPictureId();
        picture.ResetMacroblocks();
        picture.Timestamp = timestamp;
        picture.IsIdr = header.IsIdr;
        picture.FrameNum = header.FrameNum;
        picture.IsReference = header.NalReferenceIdc != 0;
        _currentFirstSlice = header;
        _current = new PictureDecodeJob(picture, _entropyContexts.TryPop(out var context) ? context : new SliceEntropyContext());
        _buffer.ComputePicOrderCount(picture, header, sps);
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

    private void FinishPicture()
    {
        var job = _current;
        if (job is null)
        {
            return;
        }

        while (_jobs.Count >= _parallelism)
        {
            RetireOldestJob();
        }

        _current = null;
        var picture = job.Picture;
        var header = _currentFirstSlice!;
        IEnumerable<H264Picture> output;
        try
        {
            _buffer.Mark(picture, header, picture.Sequence);
            output = _buffer.Store(picture, header, picture.Sequence);
        }
        catch
        {
            _entropyContexts.Push(job.Context);
            picture.Release();
            throw;
        }

        foreach (var ready in output)
        {
            ready.Hold();
            _pendingOutput.Enqueue(ready);
        }

        job.Start(_parallelism == 1);
        _jobs.Enqueue(job);
    }

    private void RetireOldestJob()
    {
        var job = _jobs.Peek();
        job.Wait();
        if (job.Error is { } error)
        {
            CancelAndDropPictures();
            ExceptionDispatchInfo.Throw(error is OperationCanceledException ? new InvalidDataException("H.264: a reference picture failed to decode") : error);
        }

        _jobs.Dequeue();
        job.ReleaseHolds();
        _entropyContexts.Push(job.Context);
    }

    private void RetireFinishedJobs()
    {
        while (_jobs.TryPeek(out var job) && job.IsCompleted)
        {
            RetireOldestJob();
        }
    }

    private void EmitDecoded(Action<VideoFrame> output)
    {
        RetireFinishedJobs();
        while (_pendingOutput.TryPeek(out var picture) && picture.IsDecoded)
        {
            _pendingOutput.Dequeue();
            try
            {
                output(CopyForOutput(picture));
            }
            finally
            {
                picture.Release();
            }
        }

        ReleaseDroppedPictures();
    }

    private void FlushPictures(Action<VideoFrame> output)
    {
        while (_jobs.Count > 0)
        {
            RetireOldestJob();
        }

        foreach (var picture in _buffer.Flush())
        {
            picture.Hold();
            _pendingOutput.Enqueue(picture);
        }

        EmitDecoded(output);
    }

    private void ReleaseDroppedPictures()
    {
        foreach (var released in _buffer.TakeReleased())
        {
            released.Release();
        }
    }

    private VideoFrame CopyForOutput(H264Picture picture)
    {
        var sps = picture.Sequence;
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
        if (_current is not { } job)
        {
            return;
        }

        _current = null;
        _entropyContexts.Push(job.Context);
        job.Picture.Release();
    }

    private void CancelAndDropPictures()
    {
        AbandonPicture();
        foreach (var job in _jobs)
        {
            job.Cancel();
        }

        while (_jobs.TryDequeue(out var job))
        {
            job.Wait();
            job.ReleaseHolds();
            _entropyContexts.Push(job.Context);
        }

        while (_pendingOutput.TryDequeue(out var picture))
        {
            picture.Release();
        }

        _buffer.Clear();
        ReleaseDroppedPictures();
    }
}
