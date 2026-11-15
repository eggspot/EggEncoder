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
    private const int WavefrontSpinRounds = 16;

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
    private readonly int _parallelism;
    private readonly int _maxDelayedFrames;
    private readonly Queue<PendingFrame> _pending = new();
    private readonly Stack<PendingFrame> _spareFrames = new();
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

        if (settings.MaxDelayedFrames is < 0 or > H264EncoderSettings.MaxDelayedFramesLimit)
        {
            throw new ArgumentException($"MaxDelayedFrames must be between 0 and {H264EncoderSettings.MaxDelayedFramesLimit}", nameof(settings));
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
        _parallelism = settings.MaxDegreeOfParallelism > 0 ? settings.MaxDegreeOfParallelism : Environment.ProcessorCount;
        _maxDelayedFrames = settings.MaxDelayedFrames;
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

    /// <summary>
    /// Encodes one frame. Encoded frames reach <paramref name="output"/> in input order: this frame's
    /// before this call returns when <see cref="H264EncoderSettings.MaxDelayedFrames"/> is 0, otherwise
    /// during a later <see cref="Encode"/> or <see cref="Flush"/>, at most that many frames later.
    /// </summary>
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

        HandOutFinishedFrames(output);
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
        var pending = RentPendingFrame();
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
            pending.Bits = new BitWriter();
            pending.NalHeader = (header.NalReferenceIdc << 5) | (keyframe ? 5 : 1);
            pending.IsKeyframe = keyframe;
            pending.Timestamp = frame.Timestamp;
            WriteSliceHeader(pending.Bits, header, keyframe);
            EncodeMacroblocks(picture, header, references, qp, keyframe, pending);
            picture.Hold();
            pending.Pictures.Add(picture);
            foreach (var reference in references)
            {
                reference.Hold();
                pending.Pictures.Add(reference);
            }

            _pending.Enqueue(pending);
            pending = null;
            ExtendBorders(picture.Frame);
            _halfPel = HalfPelPlanes.Build(picture, _parallelism, _halfPel);
        }
        catch
        {
            if (pending is not null)
            {
                _spareFrames.Push(pending);
            }

            picture.Hold();
            picture.Release();
            throw;
        }

        picture.Hold();
        _reference?.Release();
        _reference = picture;
        if (keyframe)
        {
            _idrPictureId = (_idrPictureId + 1) % 65536;
        }

        _frameNum = (_frameNum + 1) % (1 << Log2MaxFrameNum);
        _frameIndex++;
        while (_pending.Count > _maxDelayedFrames)
        {
            HandOutOldestFrame(output);
        }

        HandOutFinishedFrames(output);
    }

    /// <summary>Hands out, in order, every frame still held back (see <see cref="H264EncoderSettings.MaxDelayedFrames"/>).</summary>
    /// <exception cref="ObjectDisposedException">The encoder was disposed.</exception>
    public void Flush(Action<H264EncodedFrame> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(output);
        while (_pending.Count > 0)
        {
            HandOutOldestFrame(output);
        }
    }

    /// <summary>Returns the encoder's buffers.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        while (_pending.TryDequeue(out var pending))
        {
            try
            {
                pending.Writer?.Wait();
            }
            catch (AggregateException)
            {
            }

            pending.ReleasePictures();
        }

        _reference?.Release();
        _reference = null;
        _source.Dispose();
    }

    private PendingFrame RentPendingFrame()
    {
        if (_spareFrames.TryPop(out var spare))
        {
            return spare;
        }

        return new PendingFrame(_sps.MacroblockCount, _pps.EntropyCodingCabac);
    }

    private void HandOutFinishedFrames(Action<H264EncodedFrame> output)
    {
        while (_pending.TryPeek(out var oldest) && (oldest.Writer?.IsCompleted ?? true))
        {
            HandOutOldestFrame(output);
        }
    }

    private void HandOutOldestFrame(Action<H264EncodedFrame> output)
    {
        var pending = _pending.Dequeue();
        H264EncodedFrame encoded;
        try
        {
            pending.Writer?.GetAwaiter().GetResult();
            var nal = new List<byte>();
            NalUnitWriter.AppendLengthPrefixed(nal, pending.NalHeader, pending.Bits!.ToArray());
            encoded = new H264EncodedFrame(nal.ToArray(), pending.IsKeyframe, pending.Timestamp, _settings.FrameRate.FrameDuration);
        }
        finally
        {
            pending.ReleasePictures();
            pending.Writer = null;
            pending.Bits = null;
            _spareFrames.Push(pending);
        }

        output(encoded);
    }

    private void EncodeMacroblocks(H264Picture picture, SliceHeader header, H264Picture[] references, int qp, bool keyframe, PendingFrame pending)
    {
        var bits = pending.Bits!;
        var syntaxes = pending.Syntaxes;
        var skipped = pending.Skipped;
        var cabac = pending.Cabac;
        var cabacState = pending.CabacState;
        var width = _sps.WidthInMacroblocks;
        var rows = _sps.HeightInMacroblocks;
        var workers = Math.Clamp(_parallelism, 1, rows);
        var progress = new RowProgress(rows, WavefrontSpinRounds);
        var nextRow = -1;
        var encoders = new MacroblockEncoder[workers + 1];
        var slices = new SliceDecoder[workers + 1];
        for (var i = 0; i <= workers; i++)
        {
            slices[i] = new SliceDecoder(picture, _sps, _pps, header, 0, new H264BitReader([0x80]), _dequantizer, references, [], null, cabacState);
            encoders[i] = new MacroblockEncoder(slices[i], picture, _source, keyframe ? null : _reference, keyframe ? null : _halfPel, qp, _dequantizer, _pps.ChromaQpIndexOffset, _pps.Transform8x8Mode);
        }

        SliceFilterParameters[] filters = [new SliceFilterParameters(0, 0, 0, _pps.ChromaQpIndexOffset, _pps.SecondChromaQpIndexOffset)];

        Task Run(Action stage) => Task.Factory.StartNew(
            () =>
            {
                try
                {
                    stage();
                }
                catch
                {
                    progress.Cancel();
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
                        progress.WaitFor(row - 1, Math.Min(x + 2, width));
                    }

                    var address = (row * width) + x;
                    slices[worker].UseSyntax(syntaxes[address]);
                    skipped[address] = encoders[worker].Analyse(address);
                    progress.Report(row, x + 1);
                }
            }
        }

        void Write() => MacroblockEncoder.WriteSlice(bits, cabac, header.Type, qp, encoders[workers], syntaxes, skipped, address => progress.WaitFor(address / width, (address % width) + 1));

        void Deblock()
        {
            for (var row = 0; row < rows; row++)
            {
                progress.WaitFor(Math.Min(row + 1, rows - 1), width);
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

        var tasks = new Task[workers + 1];
        for (var i = 0; i < workers; i++)
        {
            var worker = i;
            tasks[i] = Run(() => Analyse(worker));
        }

        tasks[workers] = Run(Deblock);
        var writer = Run(Write);
        try
        {
            Task.WaitAll(tasks);
        }
        catch (AggregateException e)
        {
            try
            {
                writer.Wait();
            }
            catch (AggregateException)
            {
            }

            var cause = e.InnerExceptions.FirstOrDefault(inner => inner is not OperationCanceledException) ?? e.InnerExceptions[0];
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cause).Throw();
        }

        pending.Writer = writer;
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

internal sealed class PendingFrame
{
    public PendingFrame(int macroblockCount, bool cabac)
    {
        Syntaxes = new MacroblockSyntax[macroblockCount];
        for (var i = 0; i < macroblockCount; i++)
        {
            Syntaxes[i] = new MacroblockSyntax();
        }

        Skipped = new bool[macroblockCount];
        if (cabac)
        {
            Cabac = new CabacEncoder();
            CabacState = new CabacNeighbourState(macroblockCount);
        }
    }

    public MacroblockSyntax[] Syntaxes { get; }

    public bool[] Skipped { get; }

    public CabacEncoder? Cabac { get; }

    public CabacNeighbourState? CabacState { get; }

    public List<H264Picture> Pictures { get; } = new();

    public BitWriter? Bits { get; set; }

    public Task? Writer { get; set; }

    public int NalHeader { get; set; }

    public bool IsKeyframe { get; set; }

    public TimeSpan Timestamp { get; set; }

    public void ReleasePictures()
    {
        foreach (var picture in Pictures)
        {
            picture.Release();
        }

        Pictures.Clear();
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
