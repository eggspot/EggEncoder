using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Concentus;

namespace EggEncoder.Codecs.Opus
{
    // Concentus's OpusCodecFactory probes for a native libopus/opus.dll on the host and silently
    // switches to a P/Invoke adapter if one happens to be present, falling back to its own pure
    // managed implementation otherwise (OpusCodecFactory.AttemptToUseNativeLibrary, true by
    // default). EggEncoder bundles no native Opus binary and depends on Concentus specifically
    // *because* it's pure managed code -- silently picking up some unrelated native opus library
    // that happens to be installed on a given machine would make behavior depend on host state
    // this project doesn't control or test against, same risk class NativeLibraryLoader's own
    // explicit DllImportResolver exists to avoid for libmp3lame/libFLAC. Pinned false here, once,
    // before any encoder/decoder is ever created.
    internal static class OpusRuntimeConfiguration
    {
        [ModuleInitializer]
        [SuppressMessage("Usage", "CA2255", Justification = "Must run before any OpusCodecFactory call in this assembly.")]
        internal static void Initialize()
        {
            OpusCodecFactory.AttemptToUseNativeLibrary = false;
        }
    }
}
