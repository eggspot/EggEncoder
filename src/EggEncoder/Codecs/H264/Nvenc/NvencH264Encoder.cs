using System.Runtime.InteropServices;
using EggEncoder.Media;
using EggEncoder.Video;

namespace EggEncoder.Codecs.H264.Nvenc;

/// <summary>
/// H.264 encoding on an NVIDIA GPU through NVENC, called directly in the installed driver (<c>nvEncodeAPI64.dll</c> and
/// <c>nvcuda.dll</c> on Windows, <c>libnvidia-encode.so.1</c> and <c>libcuda.so.1</c> on Linux; driver 522 or later).
/// Nothing is bundled: without a driver and GPU, <see cref="IsSupported"/> is false and the constructor throws.
/// I and P frames only, constant quality from <see cref="H264EncoderSettings.ConstantRateFactor"/> (NVENC's
/// <c>-cq</c>), an IDR every <see cref="H264EncoderSettings.KeyframeInterval"/> frames, and the settings' colour range
/// and matrix written in the VUI. <see cref="H264EncoderSettings.MaxDegreeOfParallelism"/> and
/// <see cref="H264EncoderSettings.MaxDelayedFrames"/> don't apply. Not thread-safe; one per stream.
/// </summary>
public sealed unsafe class NvencH264Encoder : IH264Encoder
{
    private const int BufferCount = 4;
    private const int SequenceParameterBufferSize = 1024;

    private static readonly Lazy<bool> Supported = new(Probe);

    private readonly NvencLibrary _api;
    private readonly H264EncoderSettings _settings;
    private readonly nint[] _inputs = new nint[BufferCount];
    private readonly nint[] _bitstreams = new nint[BufferCount];
    private readonly Queue<(int Slot, TimeSpan Timestamp, TimeSpan Duration)> _pending = new();
    private readonly AvcDecoderConfigurationRecord _configuration;
    private nint _context;
    private void* _encoder;
    private uint _bufferFormat;
    private int _nextSlot;
    private uint _frameIndex;
    private bool _disposed;

    /// <summary>Opens an encode session on an NVIDIA GPU.</summary>
    /// <param name="settings">Size, frame rate, quality, keyframe interval, profile and colour signalling.</param>
    /// <param name="preset">NVENC preset 1 (fastest) to 7 (best); 4 by default, ffmpeg's default <c>p4</c>.</param>
    /// <param name="deviceIndex">Which GPU, counted from 0.</param>
    /// <exception cref="ArgumentException">An odd width or height, a preset outside 1-7, or a negative device index.</exception>
    /// <exception cref="NotSupportedException">No NVIDIA driver or GPU, or a driver older than NVENC API 12.0.</exception>
    /// <exception cref="InvalidOperationException">The driver refused the session (for example, too many sessions open).</exception>
    public NvencH264Encoder(H264EncoderSettings settings, int preset = 4, int deviceIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Width % 2 != 0 || settings.Height % 2 != 0 || settings.Width <= 0 || settings.Height <= 0)
        {
            throw new ArgumentException("NVENC needs an even, positive width and height", nameof(settings));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(preset, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(preset, NvencApi.Presets.Length);
        ArgumentOutOfRangeException.ThrowIfNegative(deviceIndex);
        _settings = settings;
        _api = NvencLibrary.Get();
        int device;
        CheckCuda(_api.CuDeviceGet(&device, deviceIndex), "cuDeviceGet");
        nint context;
        CheckCuda(_api.CuCtxCreate(&context, 0, device), "cuCtxCreate");
        _context = context;
        nint popped;
        _api.CuCtxPopCurrent(&popped);
        try
        {
            using var scope = EnterContext();
            OpenSession();
            _bufferFormat = ChooseInputFormat();
            Initialize(preset);
            CreateBuffers();
            _configuration = ReadConfiguration();
        }
        catch
        {
            Release();
            throw;
        }
    }

    /// <summary>Whether an NVIDIA GPU with a recent enough driver can open an H.264 encode session here.</summary>
    public static bool IsSupported => Supported.Value;

    /// <inheritdoc />
    public AvcDecoderConfigurationRecord DecoderConfiguration => _configuration;

    /// <summary>Encodes one frame; its encoded frame reaches <paramref name="output"/> before this call returns.</summary>
    /// <exception cref="ArgumentException">The frame's size isn't the configured size.</exception>
    /// <exception cref="ObjectDisposedException">The encoder was disposed.</exception>
    /// <exception cref="InvalidOperationException">The driver reported an error.</exception>
    public void Encode(VideoFrame frame, Action<H264EncodedFrame> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(output);
        if (frame.Width != _settings.Width || frame.Height != _settings.Height)
        {
            throw new ArgumentException($"The frame is {frame.Width}x{frame.Height}, the encoder {_settings.Width}x{_settings.Height}", nameof(frame));
        }

        if (_pending.Count == BufferCount)
        {
            throw new InvalidOperationException("NVENC is holding more frames than this encoder buffers");
        }

        using var scope = EnterContext();
        var slot = _nextSlot;
        _nextSlot = (_nextSlot + 1) % BufferCount;
        var pitch = CopyIntoInput(frame, _inputs[slot]);
        var picture = new Block(NvencApi.PictureParams.Size);
        try
        {
            picture.Write(0, NvencApi.PictureParams.Version);
            picture.Write(NvencApi.PictureParams.InputWidth, (uint)_settings.Width);
            picture.Write(NvencApi.PictureParams.InputHeight, (uint)_settings.Height);
            picture.Write(NvencApi.PictureParams.InputPitch, (uint)pitch);
            picture.Write(NvencApi.PictureParams.FrameIndex, _frameIndex);
            picture.Write(NvencApi.PictureParams.InputTimeStamp, (ulong)_frameIndex);
            picture.Write(NvencApi.PictureParams.InputDuration, 1UL);
            picture.Write(NvencApi.PictureParams.InputBuffer, _inputs[slot]);
            picture.Write(NvencApi.PictureParams.OutputBitstream, _bitstreams[slot]);
            picture.Write(NvencApi.PictureParams.BufferFormat, _bufferFormat);
            picture.Write(NvencApi.PictureParams.PictureStruct, (uint)NvencApi.PictureStructFrame);
            _frameIndex++;
            _pending.Enqueue((slot, frame.Timestamp, frame.Duration));
            var status = _api.EncodePicture(_encoder, picture.Pointer);
            if (status == NvencApi.NeedMoreInput)
            {
                return;
            }

            Check(status, "nvEncEncodePicture");
        }
        finally
        {
            picture.Free();
        }

        Drain(output);
    }

    /// <summary>Hands out every frame NVENC still holds; call once after the last <see cref="Encode"/>.</summary>
    /// <exception cref="ObjectDisposedException">The encoder was disposed.</exception>
    /// <exception cref="InvalidOperationException">The driver reported an error.</exception>
    public void Flush(Action<H264EncodedFrame> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(output);
        using var scope = EnterContext();
        var picture = new Block(NvencApi.PictureParams.Size);
        try
        {
            picture.Write(0, NvencApi.PictureParams.Version);
            picture.Write(NvencApi.PictureParams.EncodePicFlags, (uint)NvencApi.PictureFlagEndOfStream);
            Check(_api.EncodePicture(_encoder, picture.Pointer), "nvEncEncodePicture (end of stream)");
        }
        finally
        {
            picture.Free();
        }

        Drain(output);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Release();
    }

    private static bool Probe()
    {
        if (!NvencLibrary.TryGet(out _, out _))
        {
            return false;
        }

        try
        {
            using var probe = new NvencH264Encoder(new H264EncoderSettings { Width = 256, Height = 256, FrameRate = new VideoFrameRate(30) });
            return true;
        }
        catch (Exception error) when (error is NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void CheckCuda(int status, string call)
    {
        if (status != 0)
        {
            throw new InvalidOperationException($"CUDA {call} failed with error {status}");
        }
    }

    private static Guid ProfileGuid(H264Profile profile) => profile switch
    {
        H264Profile.Baseline => NvencApi.ProfileBaseline,
        H264Profile.Main => NvencApi.ProfileMain,
        _ => NvencApi.ProfileHigh
    };

    private static (uint Primaries, uint Transfer, uint Matrix) ColourDescription(VideoColorMatrix matrix) =>
        matrix == VideoColorMatrix.Bt709 ? (1u, 1u, 1u) : (6u, 6u, 6u);

    private static byte[] WithoutParameterSets(byte[] lengthPrefixed)
    {
        var units = AnnexBConverter.SplitLengthPrefixed(lengthPrefixed, 4);
        if (!units.Exists(unit => IsParameterSetOrDelimiter(lengthPrefixed[unit.Offset])))
        {
            return lengthPrefixed;
        }

        using var kept = new MemoryStream(lengthPrefixed.Length);
        foreach (var (offset, length) in units)
        {
            if (!IsParameterSetOrDelimiter(lengthPrefixed[offset]))
            {
                kept.Write(lengthPrefixed, offset - 4, length + 4);
            }
        }

        return kept.ToArray();
    }

    private static bool IsParameterSetOrDelimiter(byte header) => (header & 0x1F) is 7 or 8 or 9;

    private void OpenSession()
    {
        var open = new Block(NvencApi.OpenSessionParams.Size);
        try
        {
            open.Write(0, NvencApi.OpenSessionParams.Version);
            open.Write(NvencApi.OpenSessionParams.DeviceType, (uint)NvencApi.DeviceTypeCuda);
            open.Write(NvencApi.OpenSessionParams.Device, _context);
            open.Write(NvencApi.OpenSessionParams.ApiVersion, NvencApi.ApiVersion);
            void* encoder;
            var status = _api.OpenEncodeSessionEx(open.Pointer, &encoder);
            if (status != NvencApi.Success)
            {
                throw new InvalidOperationException($"NVENC refused to open an encode session (status {status}); the GPU may have no free sessions");
            }

            _encoder = encoder;
        }
        finally
        {
            open.Free();
        }
    }

    private uint ChooseInputFormat()
    {
        uint count;
        Check(_api.GetInputFormatCount(_encoder, NvencApi.CodecH264, &count), "nvEncGetInputFormatCount");
        var formats = new uint[count];
        uint returned;
        fixed (uint* pointer = formats)
        {
            Check(_api.GetInputFormats(_encoder, NvencApi.CodecH264, pointer, count, &returned), "nvEncGetInputFormats");
        }

        var listed = formats.AsSpan(0, (int)Math.Min(count, returned));
        if (listed.Contains(NvencApi.BufferFormatIyuv))
        {
            return NvencApi.BufferFormatIyuv;
        }

        return listed.Contains(NvencApi.BufferFormatNv12)
            ? NvencApi.BufferFormatNv12
            : throw new NotSupportedException("This GPU's H.264 encoder takes neither IYUV nor NV12 input");
    }

    private void Initialize(int preset)
    {
        var presetConfig = new Block(NvencApi.PresetConfig.Size);
        var config = new Block(NvencApi.Config.Size);
        var initialize = new Block(NvencApi.InitializeParams.Size);
        try
        {
            presetConfig.Write(0, NvencApi.PresetConfig.Version);
            presetConfig.Write(NvencApi.PresetConfig.Config, NvencApi.Config.Version);
            Check(_api.GetEncodePresetConfigEx(_encoder, NvencApi.CodecH264, NvencApi.Presets[preset - 1], NvencApi.TuningHighQuality, presetConfig.Pointer), "nvEncGetEncodePresetConfigEx");
            Buffer.MemoryCopy(presetConfig.Pointer + NvencApi.PresetConfig.Config, config.Pointer, NvencApi.Config.Size, NvencApi.Config.Size);
            Configure(config);

            initialize.Write(0, NvencApi.InitializeParams.Version);
            initialize.Write(NvencApi.InitializeParams.EncodeGuid, NvencApi.CodecH264);
            initialize.Write(NvencApi.InitializeParams.PresetGuid, NvencApi.Presets[preset - 1]);
            initialize.Write(NvencApi.InitializeParams.EncodeWidth, (uint)_settings.Width);
            initialize.Write(NvencApi.InitializeParams.EncodeHeight, (uint)_settings.Height);
            initialize.Write(NvencApi.InitializeParams.DarWidth, (uint)_settings.Width);
            initialize.Write(NvencApi.InitializeParams.DarHeight, (uint)_settings.Height);
            initialize.Write(NvencApi.InitializeParams.FrameRateNum, (uint)_settings.FrameRate.Numerator);
            initialize.Write(NvencApi.InitializeParams.FrameRateDen, (uint)_settings.FrameRate.Denominator);
            initialize.Write(NvencApi.InitializeParams.EnableEncodeAsync, 0u);
            initialize.Write(NvencApi.InitializeParams.EnablePtd, 1u);
            initialize.Write(NvencApi.InitializeParams.EncodeConfig, (nint)config.Pointer);
            initialize.Write(NvencApi.InitializeParams.MaxEncodeWidth, (uint)_settings.Width);
            initialize.Write(NvencApi.InitializeParams.MaxEncodeHeight, (uint)_settings.Height);
            initialize.Write(NvencApi.InitializeParams.TuningInfo, (uint)NvencApi.TuningHighQuality);
            Check(_api.InitializeEncoder(_encoder, initialize.Pointer), "nvEncInitializeEncoder");
        }
        finally
        {
            presetConfig.Free();
            config.Free();
            initialize.Free();
        }
    }

    private void Configure(Block config)
    {
        var keyframeInterval = (uint)_settings.EffectiveKeyframeInterval;
        config.Write(NvencApi.Config.ProfileGuid, ProfileGuid(_settings.Profile));
        config.Write(NvencApi.Config.GopLength, keyframeInterval);
        config.Write(NvencApi.Config.FrameIntervalP, 1u);
        config.Write(NvencApi.Config.RateControlMode, (uint)NvencApi.RateControlVbr);
        config.Write(NvencApi.Config.AverageBitRate, 0u);
        config.Write(NvencApi.Config.MaxBitRate, 0u);
        config.Write(NvencApi.Config.RateControlFlags, config.Read<uint>(NvencApi.Config.RateControlFlags) & ~NvencApi.Config.RateControlLookaheadBit);
        config.Write(NvencApi.Config.LookaheadDepth, (ushort)0);
        var quality = Math.Clamp(_settings.ConstantRateFactor, 1, 51);
        var whole = Math.Floor(quality);
        config.Write(NvencApi.Config.TargetQuality, (byte)whole);
        config.Write(NvencApi.Config.TargetQualityLsb, (byte)Math.Min(255, Math.Round((quality - whole) * 256)));

        var flags = config.Read<uint>(NvencApi.Config.H264Flags);
        flags &= ~(NvencApi.Config.H264RepeatSpsPpsBit | NvencApi.Config.H264OutputAudBit);
        flags |= NvencApi.Config.H264DisableSpsPpsBit;
        config.Write(NvencApi.Config.H264Flags, flags);
        config.Write(NvencApi.Config.H264Level, 0u);
        config.Write(NvencApi.Config.H264IdrPeriod, keyframeInterval);
        config.Write(NvencApi.Config.H264EntropyCodingMode, (uint)(_settings.Profile == H264Profile.Baseline ? NvencApi.EntropyCavlc : NvencApi.EntropyCabac));
        config.Write(NvencApi.Config.H264AdaptiveTransformMode, (uint)(_settings.Profile == H264Profile.High ? NvencApi.AdaptiveTransformEnable : NvencApi.AdaptiveTransformDisable));
        config.Write(NvencApi.Config.H264ChromaFormatIdc, 1u);

        var range = _settings.ColorRange ?? VideoColorRange.Limited;
        var matrix = _settings.ColorMatrix ?? (_settings.Height < 720 ? VideoColorMatrix.Bt601 : VideoColorMatrix.Bt709);
        var (primaries, transfer, coefficients) = ColourDescription(matrix);
        config.Write(NvencApi.Config.VuiVideoSignalTypePresent, 1u);
        config.Write(NvencApi.Config.VuiVideoFormat, 5u);
        config.Write(NvencApi.Config.VuiFullRange, range == VideoColorRange.Full ? 1u : 0u);
        config.Write(NvencApi.Config.VuiColourDescriptionPresent, 1u);
        config.Write(NvencApi.Config.VuiColourPrimaries, primaries);
        config.Write(NvencApi.Config.VuiTransferCharacteristics, transfer);
        config.Write(NvencApi.Config.VuiColourMatrix, coefficients);
    }

    private void CreateBuffers()
    {
        for (var slot = 0; slot < BufferCount; slot++)
        {
            var input = new Block(NvencApi.CreateInputBuffer.Size);
            var bitstream = new Block(NvencApi.CreateBitstreamBuffer.Size);
            try
            {
                input.Write(0, NvencApi.CreateInputBuffer.Version);
                input.Write(NvencApi.CreateInputBuffer.Width, (uint)_settings.Width);
                input.Write(NvencApi.CreateInputBuffer.Height, (uint)_settings.Height);
                input.Write(NvencApi.CreateInputBuffer.BufferFormat, _bufferFormat);
                Check(_api.CreateInputBuffer(_encoder, input.Pointer), "nvEncCreateInputBuffer");
                _inputs[slot] = input.Read<nint>(NvencApi.CreateInputBuffer.InputBuffer);
                bitstream.Write(0, NvencApi.CreateBitstreamBuffer.Version);
                Check(_api.CreateBitstreamBuffer(_encoder, bitstream.Pointer), "nvEncCreateBitstreamBuffer");
                _bitstreams[slot] = bitstream.Read<nint>(NvencApi.CreateBitstreamBuffer.BitstreamBuffer);
            }
            finally
            {
                input.Free();
                bitstream.Free();
            }
        }
    }

    private AvcDecoderConfigurationRecord ReadConfiguration()
    {
        var payload = new Block(NvencApi.SequenceParamPayload.Size);
        var buffer = new Block(SequenceParameterBufferSize);
        try
        {
            uint size = 0;
            payload.Write(0, NvencApi.SequenceParamPayload.Version);
            payload.Write(NvencApi.SequenceParamPayload.InBufferSize, (uint)SequenceParameterBufferSize);
            payload.Write(NvencApi.SequenceParamPayload.SpsPpsBuffer, (nint)buffer.Pointer);
            payload.Write(NvencApi.SequenceParamPayload.OutSpsPpsPayloadSize, (nint)(&size));
            Check(_api.GetSequenceParams(_encoder, payload.Pointer), "nvEncGetSequenceParams");
            var lengthPrefixed = AnnexBConverter.ToLengthPrefixed(new ReadOnlySpan<byte>(buffer.Pointer, (int)size));
            ReadOnlyMemory<byte>? sps = null;
            ReadOnlyMemory<byte>? pps = null;
            foreach (var (offset, length) in AnnexBConverter.SplitLengthPrefixed(lengthPrefixed, 4))
            {
                var unit = lengthPrefixed.AsMemory(offset, length);
                switch (lengthPrefixed[offset] & 0x1F)
                {
                    case 7:
                        sps ??= unit;
                        break;
                    case 8:
                        pps ??= unit;
                        break;
                }
            }

            if (sps is not { Length: >= 4 } sequence || pps is not { } picture)
            {
                throw new InvalidOperationException("NVENC returned no SPS and PPS");
            }

            var profileIdc = sequence.Span[1];
            return new AvcDecoderConfigurationRecord(
                profileIdc,
                sequence.Span[2],
                sequence.Span[3],
                4,
                [sequence],
                [picture],
                profileIdc is 100 or 110 or 122 or 244 ? new AvcHighProfileExtension(1, 8, 8, []) : null);
        }
        finally
        {
            payload.Free();
            buffer.Free();
        }
    }

    private int CopyIntoInput(VideoFrame frame, nint input)
    {
        var lockParams = new Block(NvencApi.LockInputBuffer.Size);
        try
        {
            lockParams.Write(0, NvencApi.LockInputBuffer.Version);
            lockParams.Write(NvencApi.LockInputBuffer.InputBuffer, input);
            Check(_api.LockInputBuffer(_encoder, lockParams.Pointer), "nvEncLockInputBuffer");
            try
            {
                var data = (byte*)lockParams.Read<nint>(NvencApi.LockInputBuffer.BufferDataPtr);
                var pitch = (int)lockParams.Read<uint>(NvencApi.LockInputBuffer.Pitch);
                CopyPlanes(frame, data, pitch);
                return pitch;
            }
            finally
            {
                Check(_api.UnlockInputBuffer(_encoder, (void*)input), "nvEncUnlockInputBuffer");
            }
        }
        finally
        {
            lockParams.Free();
        }
    }

    private void CopyPlanes(VideoFrame frame, byte* data, int pitch)
    {
        var width = _settings.Width;
        var height = _settings.Height;
        for (var y = 0; y < height; y++)
        {
            frame.GetRow(VideoPlane.Y, y).CopyTo(new Span<byte>(data + ((long)y * pitch), width));
        }

        var chromaWidth = width / 2;
        var chromaHeight = height / 2;
        var chroma = data + ((long)pitch * height);
        if (_bufferFormat == NvencApi.BufferFormatIyuv)
        {
            var chromaPitch = pitch / 2;
            var v = chroma + ((long)chromaPitch * chromaHeight);
            for (var y = 0; y < chromaHeight; y++)
            {
                frame.GetRow(VideoPlane.U, y).CopyTo(new Span<byte>(chroma + ((long)y * chromaPitch), chromaWidth));
                frame.GetRow(VideoPlane.V, y).CopyTo(new Span<byte>(v + ((long)y * chromaPitch), chromaWidth));
            }

            return;
        }

        for (var y = 0; y < chromaHeight; y++)
        {
            var u = frame.GetRow(VideoPlane.U, y);
            var v = frame.GetRow(VideoPlane.V, y);
            var row = new Span<byte>(chroma + ((long)y * pitch), width);
            for (var x = 0; x < chromaWidth; x++)
            {
                row[2 * x] = u[x];
                row[(2 * x) + 1] = v[x];
            }
        }
    }

    private void Drain(Action<H264EncodedFrame> output)
    {
        while (_pending.TryDequeue(out var pending))
        {
            byte[] payload;
            bool keyframe;
            var lockParams = new Block(NvencApi.LockBitstream.Size);
            try
            {
                lockParams.Write(0, NvencApi.LockBitstream.Version);
                lockParams.Write(NvencApi.LockBitstream.OutputBitstream, _bitstreams[pending.Slot]);
                Check(_api.LockBitstream(_encoder, lockParams.Pointer), "nvEncLockBitstream");
                try
                {
                    var data = (byte*)lockParams.Read<nint>(NvencApi.LockBitstream.BitstreamBufferPtr);
                    var size = (int)lockParams.Read<uint>(NvencApi.LockBitstream.BitstreamSizeInBytes);
                    payload = WithoutParameterSets(AnnexBConverter.ToLengthPrefixed(new ReadOnlySpan<byte>(data, size)));
                    keyframe = lockParams.Read<uint>(NvencApi.LockBitstream.PictureType) == NvencApi.PictureTypeIdr;
                }
                finally
                {
                    Check(_api.UnlockBitstream(_encoder, (void*)_bitstreams[pending.Slot]), "nvEncUnlockBitstream");
                }
            }
            finally
            {
                lockParams.Free();
            }

            output(new H264EncodedFrame(payload, keyframe, pending.Timestamp, pending.Duration));
        }
    }

    private void Check(int status, string call)
    {
        if (status == NvencApi.Success)
        {
            return;
        }

        var detail = _encoder is null ? null : Marshal.PtrToStringAnsi((nint)_api.GetLastErrorString(_encoder));
        throw new InvalidOperationException($"NVENC {call} failed with status {status}{(string.IsNullOrEmpty(detail) ? string.Empty : ": " + detail)}");
    }

    private ContextScope EnterContext() => new(_api, _context);

    private void Release()
    {
        if (_context == 0)
        {
            return;
        }

        using (EnterContext())
        {
            if (_encoder is not null)
            {
                for (var slot = 0; slot < BufferCount; slot++)
                {
                    if (_inputs[slot] != 0)
                    {
                        _api.DestroyInputBuffer(_encoder, (void*)_inputs[slot]);
                        _inputs[slot] = 0;
                    }

                    if (_bitstreams[slot] != 0)
                    {
                        _api.DestroyBitstreamBuffer(_encoder, (void*)_bitstreams[slot]);
                        _bitstreams[slot] = 0;
                    }
                }

                _api.DestroyEncoder(_encoder);
                _encoder = null;
            }
        }

        _api.CuCtxDestroy(_context);
        _context = 0;
    }

    private readonly struct ContextScope : IDisposable
    {
        private readonly NvencLibrary _api;

        public ContextScope(NvencLibrary api, nint context)
        {
            _api = api;
            CheckCuda(api.CuCtxPushCurrent(context), "cuCtxPushCurrent");
        }

        public void Dispose()
        {
            nint popped;
            _api.CuCtxPopCurrent(&popped);
        }
    }

    private readonly struct Block
    {
        public Block(int size)
        {
            Pointer = (byte*)NativeMemory.AllocZeroed((nuint)size);
            Size = size;
        }

        public byte* Pointer { get; }

        public int Size { get; }

        public T Read<T>(int offset)
            where T : unmanaged => *(T*)(Pointer + offset);

        public void Write<T>(int offset, T value)
            where T : unmanaged
        {
            if (offset < 0 || offset + sizeof(T) > Size)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            *(T*)(Pointer + offset) = value;
        }

        public void Free() => NativeMemory.Free(Pointer);
    }
}
