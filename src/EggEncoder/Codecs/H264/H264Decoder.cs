using EggEncoder.Media;
using EggEncoder.Video;

namespace EggEncoder.Codecs.H264;

/// <summary>
/// Decodes H.264 video (8-bit 4:2:0, progressive) into <see cref="VideoFrame"/>s, entirely in managed code. Feed it one
/// access unit (one coded picture) per <see cref="Decode"/> call; frames come out cropped to the stream's display size,
/// carrying the access unit's timestamp and the stream's colour range and matrix. Interlaced streams, 4:2:2/4:4:4,
/// more than 8 bits, slice groups and SP/SI slices are refused with <see cref="NotSupportedException"/>; damaged data
/// throws <see cref="InvalidDataException"/>. Not thread-safe: use one decoder per stream.
/// </summary>
public sealed partial class H264Decoder : IDisposable
{
    private readonly Dictionary<int, SequenceParameterSet> _sequenceSets = new();
    private readonly Dictionary<int, PictureParameterSet> _pictureSets = new();
    private readonly VideoFramePool _pool;
    private readonly int _nalLengthSize;
    private SequenceParameterSet? _activeSequenceSet;
    private bool _disposed;

    /// <summary>A decoder for MP4-style (length-prefixed) access units, configured by an <c>avcC</c> record.</summary>
    /// <param name="avcDecoderConfiguration">The AVCDecoderConfigurationRecord: NAL length size and parameter sets.</param>
    /// <param name="pool">Where frames are rented; <see cref="VideoFramePool.Shared"/> when null.</param>
    /// <exception cref="InvalidDataException">The record or a parameter set in it is damaged.</exception>
    /// <exception cref="NotSupportedException">A parameter set uses an unsupported feature.</exception>
    public H264Decoder(ReadOnlySpan<byte> avcDecoderConfiguration, VideoFramePool? pool = null)
    {
        var record = AvcDecoderConfigurationRecord.Parse(avcDecoderConfiguration);
        _pool = pool ?? VideoFramePool.Shared;
        _nalLengthSize = record.NalLengthSize;
        foreach (var set in record.SequenceParameterSets.Concat(record.PictureParameterSets))
        {
            HandleNalUnit(set.Span, TimeSpan.Zero, null);
        }
    }

    /// <summary>A decoder for Annex B access units (start codes), with parameter sets sent in the stream.</summary>
    /// <param name="pool">Where frames are rented; <see cref="VideoFramePool.Shared"/> when null.</param>
    public H264Decoder(VideoFramePool? pool = null)
    {
        _pool = pool ?? VideoFramePool.Shared;
        _nalLengthSize = 0;
    }

    /// <summary>Display width of the active sequence, or 0 before one is known.</summary>
    public int Width => _activeSequenceSet?.Width ?? 0;

    /// <summary>Display height of the active sequence, or 0 before one is known.</summary>
    public int Height => _activeSequenceSet?.Height ?? 0;

    /// <summary>The frame rate the stream declares in its timing information, if it does.</summary>
    public VideoFrameRate? FrameRate => _activeSequenceSet?.FrameRate;

    /// <summary>The colour range of the decoded frames.</summary>
    public VideoColorRange ColorRange => _activeSequenceSet?.Range ?? VideoColorRange.Limited;

    /// <summary>The colour matrix of the decoded frames (BT.601 below 720 lines when the stream doesn't say).</summary>
    public VideoColorMatrix ColorMatrix => _activeSequenceSet?.ColorMatrix ?? VideoColorMatrix.Bt709;

    /// <summary>
    /// Decodes one access unit. Frames that are ready are passed to <paramref name="output"/> in display order and
    /// belong to the receiver; a frame may come out on a later call when pictures are reordered.
    /// </summary>
    /// <param name="accessUnit">The access unit: length-prefixed NAL units for the avcC constructor, start codes for the Annex B one.</param>
    /// <param name="timestamp">The presentation time of this access unit, carried to its frame.</param>
    /// <param name="output">Receives decoded frames.</param>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The decoder was disposed.</exception>
    /// <exception cref="InvalidDataException">The data is damaged.</exception>
    /// <exception cref="NotSupportedException">The stream uses an unsupported feature.</exception>
    public void Decode(ReadOnlySpan<byte> accessUnit, TimeSpan timestamp, Action<VideoFrame> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var data = _nalLengthSize == 0 ? AnnexBConverter.ToLengthPrefixed(accessUnit) : accessUnit.ToArray();
        var lengthSize = _nalLengthSize == 0 ? 4 : _nalLengthSize;
        try
        {
            foreach (var (offset, length) in AnnexBConverter.SplitLengthPrefixed(data, lengthSize))
            {
                HandleNalUnit(data.AsSpan(offset, length), timestamp, output);
            }

            FinishPicture(output);
        }
        catch
        {
            AbandonPicture();
            throw;
        }
    }

    /// <summary>Outputs every frame the decoder still holds, in display order. Call it at the end of the stream.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is null.</exception>
    public void Flush(Action<VideoFrame> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        ObjectDisposedException.ThrowIf(_disposed, this);
        FlushPictures(output);
    }

    /// <summary>Returns every buffer the decoder holds to its pool.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        AbandonPicture();
        ReleasePictures();
    }

    private void HandleNalUnit(ReadOnlySpan<byte> nal, TimeSpan timestamp, Action<VideoFrame>? output)
    {
        if (nal.Length < 1 || (nal[0] & 0x80) != 0)
        {
            throw new InvalidDataException("H.264: a NAL unit header is invalid");
        }

        var referenceIdc = (nal[0] >> 5) & 3;
        var type = nal[0] & 0x1F;
        switch (type)
        {
            case 1 or 5:
                DecodeSlice(H264BitReader.FromNalPayload(nal[1..]), type, referenceIdc, timestamp);
                break;
            case 7:
                var sps = SequenceParameterSet.Parse(H264BitReader.FromNalPayload(nal[1..]));
                _sequenceSets[sps.Id] = sps;
                break;
            case 8:
                var pps = PictureParameterSet.Parse(H264BitReader.FromNalPayload(nal[1..]));
                _pictureSets[pps.Id] = pps;
                break;
            case 2 or 3 or 4:
                throw new NotSupportedException("H.264: data partitioning is not supported");
        }
    }
}
