using EggEncoder.Video;

namespace EggEncoder.Codecs.H264;

public sealed partial class H264Decoder
{
    private readonly List<SliceFilterParameters> _sliceFilters = new();
    private readonly Dictionary<PictureParameterSet, (SequenceParameterSet Sps, Dequantizer Dequantizer)> _dequantizers = new();
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
        var sliceNumber = _sliceFilters.Count;
        _sliceFilters.Add(new SliceFilterParameters(header.DisableDeblockingFilterIdc, header.FilterOffsetA, header.FilterOffsetB, pps.ChromaQpIndexOffset, pps.SecondChromaQpIndexOffset));
        new SliceDecoder(_current!, sps, pps, header, sliceNumber, reader, dequantizer).Decode();
    }

    private void StartPicture(SliceHeader header, SequenceParameterSet sps, TimeSpan timestamp)
    {
        _activeSequenceSet = sps;
        _current = new H264Picture(_pool, sps);
        _current.ResetMacroblocks();
        _current.Timestamp = timestamp;
        _current.IsIdr = header.IsIdr;
        _current.FrameNum = header.FrameNum;
        _current.IsReference = header.NalReferenceIdc != 0;
        _currentFirstSlice = header;
        _sliceFilters.Clear();
    }

    private Dequantizer DequantizerFor(PictureParameterSet pps, SequenceParameterSet sps)
    {
        if (_dequantizers.TryGetValue(pps, out var cached) && cached.Sps == sps)
        {
            return cached.Dequantizer;
        }

        var dequantizer = new Dequantizer(pps.ScalingListsFor(sps));
        _dequantizers[pps] = (sps, dequantizer);
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
        try
        {
            if (Array.IndexOf(picture.Kinds, MacroblockKind.NotDecoded) >= 0)
            {
                throw new InvalidDataException("H.264: the slices of a picture don't cover every macroblock");
            }

            DeblockingFilter.Apply(picture, _sliceFilters);
            output(CopyForOutput(picture, _activeSequenceSet!));
        }
        finally
        {
            picture.Dispose();
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
        _current?.Dispose();
        _current = null;
    }

    private void FlushPictures(Action<VideoFrame> output)
    {
    }

    private void ReleasePictures()
    {
    }
}
