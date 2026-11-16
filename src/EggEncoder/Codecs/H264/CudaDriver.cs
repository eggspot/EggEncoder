using System.Runtime.InteropServices;

namespace EggEncoder.Codecs.H264;

internal sealed unsafe class CudaDriver
{
    public const int MemoryTypeHost = 1;
    public const int MemoryTypeDevice = 2;

    private static readonly Lazy<CudaDriver?> Shared = new(LoadOrRecordFailure);
    private static string? _loadFailure;

    public delegate* unmanaged<uint, int> CuInit;

    public delegate* unmanaged<int*, int> CuDeviceGetCount;

    public delegate* unmanaged<int*, int, int> CuDeviceGet;

    public delegate* unmanaged<nint*, uint, int, int> CuCtxCreate;

    public delegate* unmanaged<nint, int> CuCtxDestroy;

    public delegate* unmanaged<nint, int> CuCtxPushCurrent;

    public delegate* unmanaged<nint*, int> CuCtxPopCurrent;

    public delegate* unmanaged<void*, int> CuMemcpy2D;

    private CudaDriver()
    {
    }

    public static bool TryGet(out CudaDriver? driver, out string? failure)
    {
        driver = Shared.Value;
        failure = _loadFailure;
        return driver is not null;
    }

    public nint CreateContext(int deviceIndex)
    {
        int device;
        Check(CuDeviceGet(&device, deviceIndex), "cuDeviceGet");
        nint context;
        Check(CuCtxCreate(&context, 0, device), "cuCtxCreate");
        nint popped;
        CuCtxPopCurrent(&popped);
        return context;
    }

    public ContextScope Enter(nint context) => new(this, context);

    public static void Check(int status, string call)
    {
        if (status != 0)
        {
            throw new InvalidOperationException($"CUDA {call} failed with error {status}");
        }
    }

    private static CudaDriver? LoadOrRecordFailure()
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

    private static CudaDriver? Load()
    {
        var name = OperatingSystem.IsWindows() ? "nvcuda.dll" : OperatingSystem.IsLinux() ? "libcuda.so.1" : null;
        if (name is null || !Environment.Is64BitProcess)
        {
            _loadFailure = "NVIDIA GPU support is for 64-bit Windows and Linux only";
            return null;
        }

        if (!NativeLibrary.TryLoad(name, out var cuda))
        {
            _loadFailure = $"the NVIDIA driver's {name} wasn't found";
            return null;
        }

        var driver = new CudaDriver
        {
            CuInit = (delegate* unmanaged<uint, int>)Export(cuda, "cuInit"),
            CuDeviceGetCount = (delegate* unmanaged<int*, int>)Export(cuda, "cuDeviceGetCount"),
            CuDeviceGet = (delegate* unmanaged<int*, int, int>)Export(cuda, "cuDeviceGet"),
            CuCtxCreate = (delegate* unmanaged<nint*, uint, int, int>)Export(cuda, "cuCtxCreate_v2"),
            CuCtxDestroy = (delegate* unmanaged<nint, int>)Export(cuda, "cuCtxDestroy_v2"),
            CuCtxPushCurrent = (delegate* unmanaged<nint, int>)Export(cuda, "cuCtxPushCurrent_v2"),
            CuCtxPopCurrent = (delegate* unmanaged<nint*, int>)Export(cuda, "cuCtxPopCurrent_v2"),
            CuMemcpy2D = (delegate* unmanaged<void*, int>)Export(cuda, "cuMemcpy2D_v2")
        };

        if (driver.CuInit(0) != 0)
        {
            _loadFailure = "cuInit failed: no usable NVIDIA GPU";
            return null;
        }

        int devices;
        if (driver.CuDeviceGetCount(&devices) != 0 || devices == 0)
        {
            _loadFailure = "no NVIDIA GPU was found";
            return null;
        }

        return driver;
    }

    internal static void* Export(nint library, string name) =>
        NativeLibrary.TryGetExport(library, name, out var address)
            ? (void*)address
            : throw new NotSupportedException($"The NVIDIA driver library has no {name}");

    internal readonly struct ContextScope : IDisposable
    {
        private readonly CudaDriver _driver;

        public ContextScope(CudaDriver driver, nint context)
        {
            _driver = driver;
            Check(driver.CuCtxPushCurrent(context), "cuCtxPushCurrent");
        }

        public void Dispose()
        {
            nint popped;
            _driver.CuCtxPopCurrent(&popped);
        }
    }
}
