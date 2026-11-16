using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using EggEncoder.Media;
using EggEncoder.Video;

namespace EggEncoder.Codecs.H264.Nvdec;

/// <summary>
/// H.264 decoding on an NVIDIA GPU through NVDEC, called directly in the installed driver (<c>nvcuvid.dll</c> and
/// <c>nvcuda.dll</c> on Windows, <c>libnvcuvid.so.1</c> and <c>libcuda.so.1</c> on Linux). Nothing is bundled: without a
/// driver and GPU, <see cref="IsSupported"/> is false and the constructor throws. Takes the same input as
/// <see cref="H264Decoder"/> (avcC configuration, 4-byte length-prefixed access units with their presentation time) and
/// outputs 8-bit 4:2:0 frames in display order, cropped, with the stream's colour range and matrix. Frames come out up
/// to a few calls later; <see cref="Flush"/> hands out the rest. Not thread-safe; one per stream.
/// </summary>
public sealed unsafe class NvdecH264Decoder : IH264Decoder
{
    private const int DisplayDelay = 2;
    private const int ExtraDecodeSurfaces = 4;
    private const int OutputSurfaces = 2;

    private static readonly Lazy<bool> Supported = new(Probe);

    private readonly NvdecLibrary _api;
    private readonly VideoFramePool _pool;
    private readonly byte[] _parameterSets;
    private readonly GCHandle _self;
    private nint _context;
    private nint _parser;
    private nint _decoder;
    private (int Width, int Height, int SurfaceHeight, VideoColorRange Range, VideoColorMatrix Matrix) _format;
    private byte[] _chroma = [];
    private Action<VideoFrame>? _output;
    private Exception? _failure;
    private bool _sentParameterSets;
    private bool _disposed;

    /// <summary>Opens a decoder for a stream described by an avcC record.</summary>
    /// <param name="avcDecoderConfiguration">The avcC record holding the SPS and PPS.</param>
    /// <param name="pool">Where output frames are rented; a private pool when null.</param>
    /// <param name="deviceIndex">Which GPU, counted from 0.</param>
    /// <exception cref="InvalidDataException">The avcC record is damaged.</exception>
    /// <exception cref="NotSupportedException">No NVIDIA driver or GPU.</exception>
    public NvdecH264Decoder(ReadOnlySpan<byte> avcDecoderConfiguration, VideoFramePool? pool = null, int deviceIndex = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(deviceIndex);
        var configuration = AvcDecoderConfigurationRecord.Parse(avcDecoderConfiguration);
        using var parameterSets = new MemoryStream();
        foreach (var unit in configuration.SequenceParameterSets.Concat(configuration.PictureParameterSets))
        {
            parameterSets.Write([0, 0, 0, 1]);
            parameterSets.Write(unit.Span);
        }

        _parameterSets = parameterSets.ToArray();
        _pool = pool ?? new VideoFramePool();
        _api = NvdecLibrary.Get();
        _context = _api.Cuda.CreateContext(deviceIndex);
        _self = GCHandle.Alloc(this);
        try
        {
            using var scope = _api.Cuda.Enter(_context);
            CreateParser();
        }
        catch
        {
            Release();
            throw;
        }
    }

    /// <summary>Whether an NVIDIA GPU here decodes H.264 through NVDEC.</summary>
    public static bool IsSupported => Supported.Value;

    /// <summary>Decodes one access unit.</summary>
    /// <param name="accessUnit">NAL units with 4-byte length prefixes.</param>
    /// <param name="timestamp">The access unit's presentation time, carried to its frame.</param>
    /// <param name="output">Receives decoded frames, in display order; the receiver owns them.</param>
    /// <exception cref="ObjectDisposedException">The decoder was disposed.</exception>
    /// <exception cref="NotSupportedException">The stream isn't 8-bit 4:2:0.</exception>
    /// <exception cref="InvalidOperationException">The driver reported an error.</exception>
    public void Decode(ReadOnlySpan<byte> accessUnit, TimeSpan timestamp, Action<VideoFrame> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(output);
        var annexB = AnnexBConverter.ToAnnexB(accessUnit);
        if (!_sentParameterSets)
        {
            annexB = [.. _parameterSets, .. annexB];
            _sentParameterSets = true;
        }

        fixed (byte* payload = annexB)
        {
            Parse(payload, annexB.Length, NvdecApi.PacketTimestamp, timestamp.Ticks, output);
        }
    }

    /// <summary>Hands out every frame the decoder still holds; call at the end of the stream.</summary>
    /// <exception cref="ObjectDisposedException">The decoder was disposed.</exception>
    /// <exception cref="InvalidOperationException">The driver reported an error.</exception>
    public void Flush(Action<VideoFrame> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(output);
        Parse(null, 0, NvdecApi.PacketEndOfStream, 0, output);
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
        if (!NvdecLibrary.TryGet(out _, out _))
        {
            return false;
        }

        try
        {
            var settings = new H264EncoderSettings { Width = 64, Height = 64, FrameRate = new VideoFrameRate(25), Profile = H264Profile.Main };
            using var encoder = new H264Encoder(settings);
            using var source = VideoFrame.Create(64, 64, VideoColorRange.Limited, VideoColorMatrix.Bt601);
            source.Fill(new YuvColor(128, 128, 128));
            var encoded = new List<H264EncodedFrame>();
            encoder.Encode(source, encoded.Add);
            encoder.Flush(encoded.Add);
            using var decoder = new NvdecH264Decoder(encoder.DecoderConfiguration.ToArray());
            var frames = 0;
            void Count(VideoFrame frame)
            {
                frames++;
                frame.Dispose();
            }

            foreach (var frame in encoded)
            {
                decoder.Decode(frame.Payload.Span, frame.Timestamp, Count);
            }

            decoder.Flush(Count);
            return frames == encoded.Count;
        }
        catch (Exception error) when (error is NotSupportedException or InvalidOperationException or InvalidDataException)
        {
            return false;
        }
    }

    private static NvdecH264Decoder Owner(void* user) => (NvdecH264Decoder)GCHandle.FromIntPtr((nint)user).Target!;

    [UnmanagedCallersOnly]
    private static int HandleSequence(void* user, byte* format)
    {
        var decoder = Owner(user);
        try
        {
            return decoder.OnSequence(format);
        }
        catch (Exception error)
        {
            decoder._failure ??= error;
            return 0;
        }
    }

    [UnmanagedCallersOnly]
    private static int HandleDecode(void* user, void* picture)
    {
        var decoder = Owner(user);
        try
        {
            var status = decoder._api.DecodePicture(decoder._decoder, picture);
            if (status != 0)
            {
                decoder._failure ??= new InvalidOperationException($"NVDEC cuvidDecodePicture failed with error {status}");
                return 0;
            }

            return 1;
        }
        catch (Exception error)
        {
            decoder._failure ??= error;
            return 0;
        }
    }

    [UnmanagedCallersOnly]
    private static int HandleDisplay(void* user, byte* display)
    {
        var decoder = Owner(user);
        if (display is null)
        {
            return 1;
        }

        try
        {
            decoder.OnDisplay(display);
            return 1;
        }
        catch (Exception error)
        {
            decoder._failure ??= error;
            return 0;
        }
    }

    private static VideoColorMatrix Matrix(int coefficients, int height) => coefficients switch
    {
        1 => VideoColorMatrix.Bt709,
        5 or 6 => VideoColorMatrix.Bt601,
        _ => height < 720 ? VideoColorMatrix.Bt601 : VideoColorMatrix.Bt709
    };

    private void CreateParser()
    {
        var parameters = new NativeBlock(NvdecApi.ParserParams.Size);
        try
        {
            parameters.Write(NvdecApi.ParserParams.CodecType, NvdecApi.CodecH264);
            parameters.Write(NvdecApi.ParserParams.MaxNumDecodeSurfaces, 1u);
            parameters.Write(NvdecApi.ParserParams.ClockRate, (uint)TimeSpan.TicksPerSecond);
            parameters.Write(NvdecApi.ParserParams.ErrorThreshold, 100u);
            parameters.Write(NvdecApi.ParserParams.MaxDisplayDelay, (uint)DisplayDelay);
            parameters.Write(NvdecApi.ParserParams.UserData, GCHandle.ToIntPtr(_self));
            parameters.Write(NvdecApi.ParserParams.SequenceCallback, (nint)(delegate* unmanaged<void*, byte*, int>)&HandleSequence);
            parameters.Write(NvdecApi.ParserParams.DecodePicture, (nint)(delegate* unmanaged<void*, void*, int>)&HandleDecode);
            parameters.Write(NvdecApi.ParserParams.DisplayPicture, (nint)(delegate* unmanaged<void*, byte*, int>)&HandleDisplay);
            nint parser;
            var status = _api.CreateVideoParser(&parser, parameters.Pointer);
            if (status != 0)
            {
                throw new InvalidOperationException($"NVDEC cuvidCreateVideoParser failed with error {status}");
            }

            _parser = parser;
        }
        finally
        {
            parameters.Free();
        }
    }

    private void Parse(byte* payload, int length, int flags, long timestamp, Action<VideoFrame> output)
    {
        var packet = new NativeBlock(NvdecApi.SourcePacket.Size);
        _output = output;
        try
        {
            using var scope = _api.Cuda.Enter(_context);
            var longSize = NvdecApi.ULongSize;
            packet.WriteNativeULong(NvdecApi.SourcePacket.Flags, (ulong)flags, longSize);
            packet.WriteNativeULong(NvdecApi.SourcePacket.PayloadSize, (ulong)length, longSize);
            packet.Write(NvdecApi.SourcePacket.Payload, (nint)payload);
            packet.Write(NvdecApi.SourcePacket.Timestamp, timestamp);
            var status = _api.ParseVideoData(_parser, packet.Pointer);
            if (_failure is { } failure)
            {
                _failure = null;
                ExceptionDispatchInfo.Throw(failure);
            }

            if (status != 0)
            {
                throw new InvalidOperationException($"NVDEC cuvidParseVideoData failed with error {status}");
            }
        }
        finally
        {
            _output = null;
            packet.Free();
        }
    }

    private int OnSequence(byte* format)
    {
        if (*(int*)(format + NvdecApi.VideoFormat.Codec) != NvdecApi.CodecH264
            || format[NvdecApi.VideoFormat.BitDepthLumaMinus8] != 0
            || *(int*)(format + NvdecApi.VideoFormat.ChromaFormat) != NvdecApi.ChromaFormat420)
        {
            throw new NotSupportedException("NVDEC decoding here is for 8-bit 4:2:0 H.264 only");
        }

        var codedWidth = *(uint*)(format + NvdecApi.VideoFormat.CodedWidth);
        var codedHeight = *(uint*)(format + NvdecApi.VideoFormat.CodedHeight);
        var left = *(int*)(format + NvdecApi.VideoFormat.DisplayLeft);
        var top = *(int*)(format + NvdecApi.VideoFormat.DisplayTop);
        var width = *(int*)(format + NvdecApi.VideoFormat.DisplayRight) - left;
        var height = *(int*)(format + NvdecApi.VideoFormat.DisplayBottom) - top;
        var surfaceWidth = (width + 1) & ~1;
        var surfaceHeight = (height + 1) & ~1;
        var surfaces = format[NvdecApi.VideoFormat.MinNumDecodeSurfaces] + ExtraDecodeSurfaces;
        var range = (format[NvdecApi.VideoFormat.SignalFlags] & NvdecApi.VideoFormat.FullRangeBit) != 0 ? VideoColorRange.Full : VideoColorRange.Limited;
        var matrix = Matrix(format[NvdecApi.VideoFormat.MatrixCoefficients], height);
        if (_decoder != 0)
        {
            _api.DestroyDecoder(_decoder);
            _decoder = 0;
        }

        var create = new NativeBlock(NvdecApi.DecodeCreateInfo.Size);
        try
        {
            var longSize = NvdecApi.ULongSize;
            create.WriteNativeULong(NvdecApi.DecodeCreateInfo.Width, codedWidth, longSize);
            create.WriteNativeULong(NvdecApi.DecodeCreateInfo.Height, codedHeight, longSize);
            create.WriteNativeULong(NvdecApi.DecodeCreateInfo.NumDecodeSurfaces, (ulong)surfaces, longSize);
            create.Write(NvdecApi.DecodeCreateInfo.CodecType, NvdecApi.CodecH264);
            create.Write(NvdecApi.DecodeCreateInfo.ChromaFormat, NvdecApi.ChromaFormat420);
            create.WriteNativeULong(NvdecApi.DecodeCreateInfo.CreationFlags, NvdecApi.CreatePreferCuvid, longSize);
            create.WriteNativeULong(NvdecApi.DecodeCreateInfo.BitDepthMinus8, 0, longSize);
            create.WriteNativeULong(NvdecApi.DecodeCreateInfo.MaxWidth, codedWidth, longSize);
            create.WriteNativeULong(NvdecApi.DecodeCreateInfo.MaxHeight, codedHeight, longSize);
            create.Write(NvdecApi.DecodeCreateInfo.DisplayLeft, (short)left);
            create.Write(NvdecApi.DecodeCreateInfo.DisplayTop, (short)top);
            create.Write(NvdecApi.DecodeCreateInfo.DisplayRight, (short)Math.Min(codedWidth, left + surfaceWidth));
            create.Write(NvdecApi.DecodeCreateInfo.DisplayBottom, (short)Math.Min(codedHeight, top + surfaceHeight));
            create.Write(NvdecApi.DecodeCreateInfo.OutputFormat, NvdecApi.SurfaceFormatNv12);
            create.Write(NvdecApi.DecodeCreateInfo.DeinterlaceMode, NvdecApi.DeinterlaceWeave);
            create.WriteNativeULong(NvdecApi.DecodeCreateInfo.TargetWidth, (ulong)surfaceWidth, longSize);
            create.WriteNativeULong(NvdecApi.DecodeCreateInfo.TargetHeight, (ulong)surfaceHeight, longSize);
            create.WriteNativeULong(NvdecApi.DecodeCreateInfo.NumOutputSurfaces, OutputSurfaces, longSize);
            nint decoder;
            var status = _api.CreateDecoder(&decoder, create.Pointer);
            if (status != 0)
            {
                throw new InvalidOperationException($"NVDEC cuvidCreateDecoder failed with error {status} for a {codedWidth}x{codedHeight} stream");
            }

            _decoder = decoder;
        }
        finally
        {
            create.Free();
        }

        _format = (width, height, surfaceHeight, range, matrix);
        _chroma = new byte[((width + 1) / 2) * 2 * ((height + 1) / 2)];
        return surfaces;
    }

    private void OnDisplay(byte* display)
    {
        var process = new NativeBlock(NvdecApi.ProcessingParams.Size);
        VideoFrame? frame = null;
        try
        {
            process.Write(NvdecApi.ProcessingParams.ProgressiveFrame, *(int*)(display + NvdecApi.DisplayInfo.ProgressiveFrame));
            process.Write(NvdecApi.ProcessingParams.TopFieldFirst, *(int*)(display + NvdecApi.DisplayInfo.TopFieldFirst));
            ulong surface;
            uint pitch;
            var status = _api.MapVideoFrame(_decoder, *(int*)(display + NvdecApi.DisplayInfo.PictureIndex), &surface, &pitch, process.Pointer);
            if (status != 0)
            {
                throw new InvalidOperationException($"NVDEC cuvidMapVideoFrame64 failed with error {status}");
            }

            try
            {
                var (width, height, surfaceHeight, range, matrix) = _format;
                frame = _pool.Rent(width, height, range, matrix);
                frame.Timestamp = TimeSpan.FromTicks(*(long*)(display + NvdecApi.DisplayInfo.Timestamp));
                fixed (byte* luma = &frame.Buffer[frame.OffsetY])
                {
                    Copy(surface, pitch, luma, frame.StrideY, width, height);
                }

                var chromaWidth = (width + 1) / 2;
                var chromaHeight = (height + 1) / 2;
                fixed (byte* chroma = _chroma)
                {
                    Copy(surface + ((ulong)pitch * (ulong)surfaceHeight), pitch, chroma, chromaWidth * 2, chromaWidth * 2, chromaHeight);
                }

                for (var y = 0; y < chromaHeight; y++)
                {
                    var interleaved = _chroma.AsSpan(y * chromaWidth * 2, chromaWidth * 2);
                    var u = frame.GetRow(VideoPlane.U, y);
                    var v = frame.GetRow(VideoPlane.V, y);
                    for (var x = 0; x < chromaWidth; x++)
                    {
                        u[x] = interleaved[2 * x];
                        v[x] = interleaved[(2 * x) + 1];
                    }
                }
            }
            finally
            {
                _api.UnmapVideoFrame(_decoder, surface);
            }

            var output = _output!;
            var ready = frame;
            frame = null;
            output(ready);
        }
        finally
        {
            frame?.Dispose();
            process.Free();
        }
    }

    private void Copy(ulong source, uint sourcePitch, byte* destination, int destinationPitch, int widthInBytes, int height)
    {
        var copy = new NativeBlock(NvdecApi.Memcpy2D.Size);
        try
        {
            copy.Write(NvdecApi.Memcpy2D.SourceMemoryType, CudaDriver.MemoryTypeDevice);
            copy.Write(NvdecApi.Memcpy2D.SourceDevice, source);
            copy.Write(NvdecApi.Memcpy2D.SourcePitch, (nuint)sourcePitch);
            copy.Write(NvdecApi.Memcpy2D.DestinationMemoryType, CudaDriver.MemoryTypeHost);
            copy.Write(NvdecApi.Memcpy2D.DestinationHost, (nint)destination);
            copy.Write(NvdecApi.Memcpy2D.DestinationPitch, (nuint)destinationPitch);
            copy.Write(NvdecApi.Memcpy2D.WidthInBytes, (nuint)widthInBytes);
            copy.Write(NvdecApi.Memcpy2D.Height, (nuint)height);
            CudaDriver.Check(_api.Cuda.CuMemcpy2D(copy.Pointer), "cuMemcpy2D");
        }
        finally
        {
            copy.Free();
        }
    }

    private void Release()
    {
        if (_context != 0)
        {
            using (_api.Cuda.Enter(_context))
            {
                if (_parser != 0)
                {
                    _api.DestroyVideoParser(_parser);
                    _parser = 0;
                }

                if (_decoder != 0)
                {
                    _api.DestroyDecoder(_decoder);
                    _decoder = 0;
                }
            }

            _api.Cuda.CuCtxDestroy(_context);
            _context = 0;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }
}
