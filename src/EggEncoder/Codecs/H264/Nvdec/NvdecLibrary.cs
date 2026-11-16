using System.Runtime.InteropServices;

namespace EggEncoder.Codecs.H264.Nvdec;

internal sealed unsafe class NvdecLibrary
{
    private static readonly Lazy<NvdecLibrary?> Shared = new(LoadOrRecordFailure);
    private static string? _loadFailure;

    public delegate* unmanaged<nint*, void*, int> CreateVideoParser;

    public delegate* unmanaged<nint, void*, int> ParseVideoData;

    public delegate* unmanaged<nint, int> DestroyVideoParser;

    public delegate* unmanaged<nint*, void*, int> CreateDecoder;

    public delegate* unmanaged<nint, void*, int> DecodePicture;

    public delegate* unmanaged<nint, int> DestroyDecoder;

    public delegate* unmanaged<nint, int, ulong*, uint*, void*, int> MapVideoFrame;

    public delegate* unmanaged<nint, ulong, int> UnmapVideoFrame;

    private NvdecLibrary()
    {
    }

    public CudaDriver Cuda { get; private init; } = null!;

    public static NvdecLibrary Get() =>
        Shared.Value ?? throw new NotSupportedException($"NVDEC isn't available: {_loadFailure}");

    public static bool TryGet(out NvdecLibrary? library, out string? failure)
    {
        library = Shared.Value;
        failure = _loadFailure;
        return library is not null;
    }

    private static NvdecLibrary? LoadOrRecordFailure()
    {
        try
        {
            if (!CudaDriver.TryGet(out var cuda, out var cudaFailure))
            {
                _loadFailure = cudaFailure;
                return null;
            }

            var name = OperatingSystem.IsWindows() ? "nvcuvid.dll" : "libnvcuvid.so.1";
            if (!NativeLibrary.TryLoad(name, out var cuvid))
            {
                _loadFailure = $"the NVIDIA driver's {name} wasn't found";
                return null;
            }

            return new NvdecLibrary
            {
                Cuda = cuda!,
                CreateVideoParser = (delegate* unmanaged<nint*, void*, int>)CudaDriver.Export(cuvid, "cuvidCreateVideoParser"),
                ParseVideoData = (delegate* unmanaged<nint, void*, int>)CudaDriver.Export(cuvid, "cuvidParseVideoData"),
                DestroyVideoParser = (delegate* unmanaged<nint, int>)CudaDriver.Export(cuvid, "cuvidDestroyVideoParser"),
                CreateDecoder = (delegate* unmanaged<nint*, void*, int>)CudaDriver.Export(cuvid, "cuvidCreateDecoder"),
                DecodePicture = (delegate* unmanaged<nint, void*, int>)CudaDriver.Export(cuvid, "cuvidDecodePicture"),
                DestroyDecoder = (delegate* unmanaged<nint, int>)CudaDriver.Export(cuvid, "cuvidDestroyDecoder"),
                MapVideoFrame = (delegate* unmanaged<nint, int, ulong*, uint*, void*, int>)CudaDriver.Export(cuvid, "cuvidMapVideoFrame64"),
                UnmapVideoFrame = (delegate* unmanaged<nint, ulong, int>)CudaDriver.Export(cuvid, "cuvidUnmapVideoFrame64")
            };
        }
        catch (Exception error) when (error is NotSupportedException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            _loadFailure = error.Message;
            return null;
        }
    }
}
