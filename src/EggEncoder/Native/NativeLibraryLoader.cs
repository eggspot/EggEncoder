using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EggEncoder.Native
{
    internal static class NativeLibraryLoader
    {
        private static readonly string[] _managedLibraryNames = ["libFLAC"];

        [ModuleInitializer]
        [SuppressMessage("Usage", "CA2255", Justification = "Native DLL import resolution must be registered before any P/Invoke call in this assembly runs.")]
        internal static void Initialize()
        {
            NativeLibrary.SetDllImportResolver(typeof(NativeLibraryLoader).Assembly, Resolve);
        }

        private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (!_managedLibraryNames.Contains(libraryName))
            {
                return IntPtr.Zero;
            }

            var nativeLibraryPath = Path.Combine(AppContext.BaseDirectory, "Native", "win-x64", $"{libraryName}.dll");
            return NativeLibrary.Load(nativeLibraryPath);
        }
    }
}
