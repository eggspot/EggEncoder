using EggEncoder.Media;
using EggEncoder.Transform;
using EggEncoder.Video;

namespace EggEncoder.Codecs.H264;

/// <summary>
/// Encodes 8-bit 4:2:0 <see cref="VideoFrame"/>s into H.264: IDR and P frames, one slice per frame,
/// intra and inter mode decision by SATD, motion search with quarter-pel refinement, deblocking and
/// CRF-like rate control. The reconstruction matches what <see cref="H264Decoder"/> and other
/// conforming decoders produce from the output. Not thread-safe: one encoder per stream.
/// </summary>
public sealed partial class H264Encoder : IDisposable
{
    private const int Padding = H264Picture.Padding;
    private const int Log2MaxFrameNum = 8;

    private readonly H264EncoderSettings _settings;
    private readonly VideoFramePool _pool = new();
    private readonly MacroblockArrayPool _arrayPool = new();
    private readonly SequenceParameterSet _sps;
    private readonly PictureParameterSet _pps;
    private readonly byte[] _spsNal;
    private readonly byte[] _ppsNal;
    private readonly Dequantizer _dequantizer;
    private readonly H264RateControl _rateControl;
    private readonly int _keyframeInterval;
    private readonly VideoFrame _source;
    private readonly CabacEncoder? _cabac;
    private readonly CabacNeighbourState? _cabacState;
    private readonly MacroblockSyntax[] _syntaxes;
    private readonly bool[] _skipped;
    private readonly int _parallelism;
    private H264Picture? _reference;
    private HalfPelPlanes? _halfPel;
    private long _frameIndex;
    private int _frameNum;
    private int _idrPictureId;
    private int _pictureId;
    private bool _disposed;

    /// <summary>Creates an encoder; the parameter sets are fixed from <paramref name="settings"/>.</summary>
    /// <exception cref="ArgumentException">An odd or out-of-range size, a non-positive frame rate, or a rate factor outside 0-51.</exception>
    /// <exception cref="NotSupportedException">The size and frame rate need more than H.264 level 5.2 allows.</exception>
    public H264Encoder(H264EncoderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Width is < 2 or > 8192 || settings.Height is < 2 or > 8192 || settings.Width % 2 != 0 || settings.Height % 2 != 0)
        {
            throw new ArgumentException("Width and height must be even and between 2 and 8192", nameof(settings));
        }

        if (settings.FrameRate.Numerator <= 0 || settings.FrameRate.Denominator <= 0)
        {
            throw new ArgumentException("The frame rate must be positive", nameof(settings));
        }

        if (settings.ConstantRateFactor is < 0 or > 51 || double.IsNaN(settings.ConstantRateFactor))
        {
            throw new ArgumentException("ConstantRateFactor must be between 0 and 51", nameof(settings));
        }

        _settings = settings;
        _keyframeInterval = settings.KeyframeInterval > 0 ? settings.KeyframeInterval : Math.Max(1, (int)Math.Round(2 * settings.FrameRate.FramesPerSecond));
        var range = settings.ColorRange ?? VideoColorRange.Limited;
        var matrix = settings.ColorMatrix ?? (settings.Height < 720 ? VideoColorMatrix.Bt601 : VideoColorMatrix.Bt709);
        var spsRbsp = WriteSequenceParameterSet(settings, range, matrix);
        var ppsRbsp = WritePictureParameterSet(settings);
        _sps = SequenceParameterSet.Parse(new H264BitReader(spsRbsp));
        _pps = PictureParameterSet.Parse(new H264BitReader(ppsRbsp));
        _spsNal = NalUnitWriter.Build(0x67, spsRbsp);
        _ppsNal = NalUnitWriter.Build(0x68, ppsRbsp);
        _dequantizer = new Dequantizer(_pps.ScalingListsFor(_sps));
        _rateControl = new H264RateControl(settings.ConstantRateFactor);
        _source = _pool.Rent(_sps.WidthInMacroblocks * 16, _sps.HeightInMacroblocks * 16, range, matrix);
        _syntaxes = new MacroblockSyntax[_sps.MacroblockCount];
        for (var i = 0; i < _syntaxes.Length; i++)
        {
            _syntaxes[i] = new MacroblockSyntax();
        }

        _skipped = new bool[_sps.MacroblockCount];
        _parallelism = settings.MaxDegreeOfParallelism > 0 ? settings.MaxDegreeOfParallelism : Environment.ProcessorCount;
        if (_pps.EntropyCodingCabac)
        {
            _cabac = new CabacEncoder();
            _cabacState = new CabacNeighbourState(_sps.MacroblockCount);
        }
    }

    /// <summary>The <c>avcC</c> record (SPS and PPS) a container needs to play the output.</summary>
    public AvcDecoderConfigurationRecord DecoderConfiguration => new(
        _sps.ProfileIdc,
        _spsNal[2],
        _sps.LevelIdc,
        4,
        [_spsNal],
        [_ppsNal],
        _sps.ProfileIdc == 100 ? new AvcHighProfileExtension(1, 8, 8, []) : null);

    internal H264Picture? LastReconstruction => _reference;

    /// <summary>Encodes one frame and hands the result to <paramref name="output"/> before returning.</summary>
    /// <exception cref="ArgumentException">The frame's size isn't the configured size.</exception>
    /// <exception cref="ObjectDisposedException">The encoder was disposed.</exception>
    public void Encode(VideoFrame frame, Action<H264EncodedFrame> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(output);
        if (frame.Width != _settings.Width || frame.Height != _settings.Height)
        {
            throw new ArgumentException($"The frame is {frame.Width}x{frame.Height}, the encoder {_settings.Width}x{_settings.Height}", nameof(frame));
        }

        CopySource(frame);
        var keyframe = _frameIndex % _keyframeInterval == 0;
        var qp = _rateControl.NextQp(keyframe, keyframe ? 0 : Complexity());
        if (keyframe)
        {
            _frameNum = 0;
        }

        var picture = new H264Picture(_pool, _sps, _arrayPool)
        {
            PictureId = ++_pictureId,
            FrameNum = _frameNum,
            IsReference = true,
            IsIdr = keyframe
        };
        picture.ResetMacroblocks();
        var payload = new List<byte>();
        try
        {
            var header = new SliceHeader
            {
                FirstMacroblock = 0,
                Type = keyframe ? SliceType.I : SliceType.P,
                IsIdr = keyframe,
                FrameNum = _frameNum,
                NalReferenceIdc = keyframe ? 3 : 2,
                QpDelta = qp - (_pps.InitialQp),
                ReferenceCountL0 = 1
            };
            var references = keyframe ? Array.Empty<H264Picture>() : [_reference!];
            var bits = new BitWriter();
            WriteSliceHeader(bits, header, keyframe);
            EncodeMacroblocks(picture, header, references, qp, keyframe, bits);
            NalUnitWriter.AppendLengthPrefixed(payload, (header.NalReferenceIdc << 5) | (keyframe ? 5 : 1), bits.ToArray());
            ExtendBorders(picture.Frame);
            _halfPel = HalfPelPlanes.Build(picture, _parallelism, _halfPel);
        }
        catch
        {
            picture.Dispose();
            throw;
        }

        _reference?.Dispose();
        _reference = picture;
        if (keyframe)
        {
            _idrPictureId = (_idrPictureId + 1) % 65536;
        }

        _frameNum = (_frameNum + 1) % (1 << Log2MaxFrameNum);
        _frameIndex++;
        output(new H264EncodedFrame(payload.ToArray(), keyframe, frame.Timestamp, _settings.FrameRate.FrameDuration));
    }

    /// <summary>Hands out frames still held back; this encoder holds none (it writes no B frames), so it does nothing.</summary>
    public void Flush(Action<H264EncodedFrame> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(output);
    }

    /// <summary>Returns the encoder's buffers.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _reference?.Dispose();
        _reference = null;
        _source.Dispose();
    }

    private void EncodeMacroblocks(H264Picture picture, SliceHeader header, H264Picture[] references, int qp, bool keyframe, BitWriter bits)
    {
        var width = _sps.WidthInMacroblocks;
        var rows = _sps.HeightInMacroblocks;
        var workers = Math.Clamp(_parallelism, 1, rows);
        var progress = new int[rows];
        var nextRow = -1;
        var encoders = new MacroblockEncoder[workers + 1];
        var slices = new SliceDecoder[workers + 1];
        for (var i = 0; i <= workers; i++)
        {
            slices[i] = new SliceDecoder(picture, _sps, _pps, header, 0, new H264BitReader([0x80]), _dequantizer, references, [], null, _cabacState);
            encoders[i] = new MacroblockEncoder(slices[i], picture, _source, keyframe ? null : _reference, keyframe ? null : _halfPel, qp, _dequantizer, _pps.ChromaQpIndexOffset);
        }

        SliceFilterParameters[] filters = [new SliceFilterParameters(0, 0, 0, _pps.ChromaQpIndexOffset, _pps.SecondChromaQpIndexOffset)];

        var failed = 0;

        void WaitForRow(int row, int done)
        {
            var spinner = default(SpinWait);
            while (Volatile.Read(ref progress[row]) < done)
            {
                if (Volatile.Read(ref failed) != 0)
                {
                    throw new OperationCanceledException();
                }

                spinner.SpinOnce(sleep1Threshold: -1);
            }
        }

        Task Run(Action stage) => Task.Factory.StartNew(
            () =>
            {
                try
                {
                    stage();
                }
                catch
                {
                    Volatile.Write(ref failed, 1);
                    throw;
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        void Analyse(int worker)
        {
            int row;
            while ((row = Interlocked.Increment(ref nextRow)) < rows)
            {
                for (var x = 0; x < width; x++)
                {
                    if (row > 0)
                    {
                        WaitForRow(row - 1, Math.Min(x + 2, width));
                    }

                    var address = (row * width) + x;
                    slices[worker].UseSyntax(_syntaxes[address]);
                    _skipped[address] = encoders[worker].Analyse(address);
                    Volatile.Write(ref progress[row], x + 1);
                }
            }
        }

        void Write() => MacroblockEncoder.WriteSlice(bits, _cabac, header.Type, qp, encoders[workers], _syntaxes, _skipped, address => WaitForRow(address / width, (address % width) + 1));

        void Deblock()
        {
            for (var row = 0; row < rows; row++)
            {
                WaitForRow(Math.Min(row + 1, rows - 1), width);
                DeblockingFilter.ApplyRows(picture, filters, row, row + 1);
            }
        }

        if (workers == 1)
        {
            Analyse(0);
            Write();
            Deblock();
            return;
        }

        var tasks = new Task[workers + 2];
        for (var i = 0; i < workers; i++)
        {
            var worker = i;
            tasks[i] = Run(() => Analyse(worker));
        }

        tasks[workers] = Run(Write);
        tasks[workers + 1] = Run(Deblock);
        try
        {
            Task.WaitAll(tasks);
        }
        catch (AggregateException e)
        {
            var cause = e.InnerExceptions.FirstOrDefault(inner => inner is not OperationCanceledException) ?? e.InnerExceptions[0];
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cause).Throw();
        }
    }

    private void CopySource(VideoFrame frame)
    {
        var width = _source.Width;
        var height = _source.Height;
        for (var y = 0; y < height; y++)
        {
            var target = _source.GetRow(VideoPlane.Y, y);
            var row = frame.GetRow(VideoPlane.Y, Math.Min(y, frame.Height - 1));
            row.CopyTo(target);
            target[frame.Width..width].Fill(row[^1]);
        }

        for (var y = 0; y < height / 2; y++)
        {
            foreach (var plane in (ReadOnlySpan<VideoPlane>)[VideoPlane.U, VideoPlane.V])
            {
                var target = _source.GetRow(plane, y);
                var row = frame.GetRow(plane, Math.Min(y, frame.ChromaHeight - 1));
                row.CopyTo(target);
                target[frame.ChromaWidth..(width / 2)].Fill(row[^1]);
            }
        }
    }

    private double Complexity()
    {
        var reference = _reference!.Frame;
        long total = 0;
        var samples = 0;
        for (var y = 0; y < _source.Height; y += 4)
        {
            var source = _source.GetRow(VideoPlane.Y, y);
            var previous = reference.GetRow(VideoPlane.Y, y);
            for (var x = 0; x < source.Length; x += 4)
            {
                total += Math.Abs(source[x] - previous[x]);
                samples++;
            }
        }

        return samples == 0 ? 0 : total / (double)samples;
    }

    private static void ExtendBorders(VideoFrame frame)
    {
        ExtendPlane(frame.Buffer, frame.OffsetY, frame.StrideY, frame.Width, frame.Height, Padding);
        ExtendPlane(frame.Buffer, frame.OffsetU, frame.StrideUV, frame.ChromaWidth, frame.ChromaHeight, Padding / 2);
        ExtendPlane(frame.Buffer, frame.OffsetV, frame.StrideUV, frame.ChromaWidth, frame.ChromaHeight, Padding / 2);
    }

    private static void ExtendPlane(byte[] buffer, int offset, int stride, int width, int height, int padding)
    {
        for (var y = 0; y < height; y++)
        {
            var row = offset + (y * stride);
            buffer.AsSpan(row - padding, padding).Fill(buffer[row]);
            buffer.AsSpan(row + width, padding).Fill(buffer[row + width - 1]);
        }

        var first = buffer.AsSpan(offset - padding, width + (2 * padding));
        var last = buffer.AsSpan(offset + ((height - 1) * stride) - padding, width + (2 * padding));
        for (var y = 1; y <= padding; y++)
        {
            first.CopyTo(buffer.AsSpan(offset - (y * stride) - padding, width + (2 * padding)));
            last.CopyTo(buffer.AsSpan(offset + ((height - 1 + y) * stride) - padding, width + (2 * padding)));
        }
    }
}

internal sealed class H264RateControl
{
    private const double ComplexityCompression = 0.6;

    private readonly double _rateFactor;
    private double _averageComplexity;

    public H264RateControl(double rateFactor)
    {
        _rateFactor = rateFactor;
    }

    public int NextQp(bool keyframe, double complexity)
    {
        if (keyframe)
        {
            return Math.Clamp((int)Math.Round(_rateFactor) - 3, 0, 51);
        }

        var offset = 0.0;
        if (_averageComplexity > 0 && complexity > 0)
        {
            offset = Math.Clamp(6 * (1 - ComplexityCompression) * Math.Log2(complexity / _averageComplexity), -3, 3);
        }

        _averageComplexity = _averageComplexity == 0 ? complexity : (0.8 * _averageComplexity) + (0.2 * complexity);
        return Math.Clamp((int)Math.Round(_rateFactor + offset), 0, 51);
    }
}
