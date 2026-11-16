using System.Runtime.InteropServices;

namespace EggEncoder.Codecs.H264.Nvenc;

internal sealed unsafe class NvencLibrary
{
    private static readonly Lazy<NvencLibrary?> Shared = new(LoadOrRecordFailure);
    private static string? _loadFailure;

    private NvencLibrary()
    {
    }

    public delegate* unmanaged<uint, int> CuInit;

    public delegate* unmanaged<int*, int> CuDeviceGetCount;

    public delegate* unmanaged<int*, int, int> CuDeviceGet;

    public delegate* unmanaged<nint*, uint, int, int> CuCtxCreate;

    public delegate* unmanaged<nint, int> CuCtxDestroy;

    public delegate* unmanaged<nint, int> CuCtxPushCurrent;

    public delegate* unmanaged<nint*, int> CuCtxPopCurrent;

    public delegate* unmanaged<void*, void**, int> OpenEncodeSessionEx;

    public delegate* unmanaged<void*, Guid, uint*, int> GetInputFormatCount;

    public delegate* unmanaged<void*, Guid, uint*, uint, uint*, int> GetInputFormats;

    public delegate* unmanaged<void*, Guid, Guid, int, void*, int> GetEncodePresetConfigEx;

    public delegate* unmanaged<void*, void*, int> InitializeEncoder;

    public delegate* unmanaged<void*, void*, int> CreateInputBuffer;

    public delegate* unmanaged<void*, void*, int> DestroyInputBuffer;

    public delegate* unmanaged<void*, void*, int> CreateBitstreamBuffer;

    public delegate* unmanaged<void*, void*, int> DestroyBitstreamBuffer;

    public delegate* unmanaged<void*, void*, int> EncodePicture;

    public delegate* unmanaged<void*, void*, int> LockBitstream;

    public delegate* unmanaged<void*, void*, int> UnlockBitstream;

    public delegate* unmanaged<void*, void*, int> LockInputBuffer;

    public delegate* unmanaged<void*, void*, int> UnlockInputBuffer;

    public delegate* unmanaged<void*, void*, int> GetSequenceParams;

    public delegate* unmanaged<void*, int> DestroyEncoder;

    public delegate* unmanaged<void*, byte*> GetLastErrorString;

    public static NvencLibrary Get() =>
        Shared.Value ?? throw new NotSupportedException($"NVENC isn't available: {_loadFailure}");

    public static bool TryGet(out NvencLibrary? library, out string? failure)
    {
        library = Shared.Value;
        failure = _loadFailure;
        return library is not null;
    }

    private static NvencLibrary? LoadOrRecordFailure()
    {
        try
        {
            return Load();
        }
        catch (Exception error) when (error is NotSupportedException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            _loadFailure = error.Message;
            return null;
        }
    }

    private static NvencLibrary? Load()
    {
        var (cudaName, nvencName) = OperatingSystem.IsWindows()
            ? ("nvcuda.dll", "nvEncodeAPI64.dll")
            : OperatingSystem.IsLinux() ? ("libcuda.so.1", "libnvidia-encode.so.1") : (null, null);
        if (cudaName is null || nvencName is null || !Environment.Is64BitProcess)
        {
            _loadFailure = "NVENC is used on 64-bit Windows and Linux only";
            return null;
        }

        if (!NativeLibrary.TryLoad(cudaName, out var cuda) || !NativeLibrary.TryLoad(nvencName, out var nvenc))
        {
            _loadFailure = $"the NVIDIA driver's {cudaName} or {nvencName} wasn't found";
            return null;
        }

        var library = new NvencLibrary
        {
            CuInit = (delegate* unmanaged<uint, int>)Export(cuda, "cuInit"),
            CuDeviceGetCount = (delegate* unmanaged<int*, int>)Export(cuda, "cuDeviceGetCount"),
            CuDeviceGet = (delegate* unmanaged<int*, int, int>)Export(cuda, "cuDeviceGet"),
            CuCtxCreate = (delegate* unmanaged<nint*, uint, int, int>)Export(cuda, "cuCtxCreate_v2"),
            CuCtxDestroy = (delegate* unmanaged<nint, int>)Export(cuda, "cuCtxDestroy_v2"),
            CuCtxPushCurrent = (delegate* unmanaged<nint, int>)Export(cuda, "cuCtxPushCurrent_v2"),
            CuCtxPopCurrent = (delegate* unmanaged<nint*, int>)Export(cuda, "cuCtxPopCurrent_v2")
        };

        var getMaxVersion = (delegate* unmanaged<uint*, int>)Export(nvenc, "NvEncodeAPIGetMaxSupportedVersion");
        uint driverVersion = 0;
        if (getMaxVersion(&driverVersion) != NvencApi.Success || driverVersion < NvencApi.MinimumDriverApiVersion)
        {
            _loadFailure = $"the NVIDIA driver supports NVENC API {driverVersion >> 4}.{driverVersion & 0xF}; 12.0 or later is needed";
            return null;
        }

        var createInstance = (delegate* unmanaged<void*, int>)Export(nvenc, "NvEncodeAPICreateInstance");
        var functions = (byte*)NativeMemory.AllocZeroed(NvencApi.FunctionList.Size);
        try
        {
            *(uint*)functions = NvencApi.FunctionList.Version;
            if (createInstance(functions) != NvencApi.Success)
            {
                _loadFailure = "NvEncodeAPICreateInstance failed";
                return null;
            }

            void* Function(int offset) => *(void**)(functions + offset);
            library.OpenEncodeSessionEx = (delegate* unmanaged<void*, void**, int>)Function(NvencApi.FunctionList.OpenEncodeSessionEx);
            library.GetInputFormatCount = (delegate* unmanaged<void*, Guid, uint*, int>)Function(NvencApi.FunctionList.GetInputFormatCount);
            library.GetInputFormats = (delegate* unmanaged<void*, Guid, uint*, uint, uint*, int>)Function(NvencApi.FunctionList.GetInputFormats);
            library.GetEncodePresetConfigEx = (delegate* unmanaged<void*, Guid, Guid, int, void*, int>)Function(NvencApi.FunctionList.GetEncodePresetConfigEx);
            library.InitializeEncoder = (delegate* unmanaged<void*, void*, int>)Function(NvencApi.FunctionList.InitializeEncoder);
            library.CreateInputBuffer = (delegate* unmanaged<void*, void*, int>)Function(NvencApi.FunctionList.CreateInputBuffer);
            library.DestroyInputBuffer = (delegate* unmanaged<void*, void*, int>)Function(NvencApi.FunctionList.DestroyInputBuffer);
            library.CreateBitstreamBuffer = (delegate* unmanaged<void*, void*, int>)Function(NvencApi.FunctionList.CreateBitstreamBuffer);
            library.DestroyBitstreamBuffer = (delegate* unmanaged<void*, void*, int>)Function(NvencApi.FunctionList.DestroyBitstreamBuffer);
            library.EncodePicture = (delegate* unmanaged<void*, void*, int>)Function(NvencApi.FunctionList.EncodePicture);
            library.LockBitstream = (delegate* unmanaged<void*, void*, int>)Function(NvencApi.FunctionList.LockBitstream);
            library.UnlockBitstream = (delegate* unmanaged<void*, void*, int>)Function(NvencApi.FunctionList.UnlockBitstream);
            library.LockInputBuffer = (delegate* unmanaged<void*, void*, int>)Function(NvencApi.FunctionList.LockInputBuffer);
            library.UnlockInputBuffer = (delegate* unmanaged<void*, void*, int>)Function(NvencApi.FunctionList.UnlockInputBuffer);
            library.GetSequenceParams = (delegate* unmanaged<void*, void*, int>)Function(NvencApi.FunctionList.GetSequenceParams);
            library.DestroyEncoder = (delegate* unmanaged<void*, int>)Function(NvencApi.FunctionList.DestroyEncoder);
            library.GetLastErrorString = (delegate* unmanaged<void*, byte*>)Function(NvencApi.FunctionList.GetLastErrorString);
        }
        finally
        {
            NativeMemory.Free(functions);
        }

        if (library.CuInit(0) != 0)
        {
            _loadFailure = "cuInit failed: no usable NVIDIA GPU";
            return null;
        }

        int devices;
        if (library.CuDeviceGetCount(&devices) != 0 || devices == 0)
        {
            _loadFailure = "no NVIDIA GPU was found";
            return null;
        }

        return library;
    }

    private static void* Export(nint library, string name) =>
        NativeLibrary.TryGetExport(library, name, out var address)
            ? (void*)address
            : throw new NotSupportedException($"The NVIDIA driver library has no {name}");
}
