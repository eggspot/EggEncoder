using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EggEncoder.Native
{
    // Mirrors the real WavpackConfig struct from WavPack's own public wavpack.h exactly, field-for-field
    // (including the two fields this project never touches, md5_checksum/md5_read/num_tag_strings/
    // tag_strings) -- WavpackSetConfiguration64 receives a pointer to this struct and may read past
    // whatever fields a caller happens to care about, so under-declaring it would let native code read
    // past the end of a too-small managed allocation. Sequential layout's default alignment matches the
    // platform C ABI's natural alignment (the same assumption this project already relies on implicitly
    // for every other blittable P/Invoke parameter), so no explicit FieldOffset/Pack is needed.
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct WavpackConfig
    {
        public float Bitrate;
        public float ShapingWeight;
        public int BitsPerSample;
        public int BytesPerSample;
        public int QMode;
        public int Flags;
        public int XMode;
        public int NumChannels;
        public int FloatNormExp;
        public int BlockSamples;
        public int WorkerThreads;
        public int SampleRate;
        public int ChannelMask;
        public fixed byte Md5Checksum[16];
        public byte Md5Read;
        public int NumTagStrings;
        public IntPtr TagStrings;
    }

    internal static partial class WavPackNative
    {
        // "assume filenames are UTF-8 encoded, not ANSI" -- WavpackOpenFileInput defaults to ANSI
        // filenames on Windows without this flag, which would corrupt any non-ASCII path; always set.
        public const int OpenFileUtf8 = 0x80;

        public const int ModeLossless = 0x2;
        public const int ModeFloat = 0x8;

        [LibraryImport("wavpackdll", EntryPoint = "WavpackOpenFileInput", StringMarshalling = StringMarshalling.Utf8)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial IntPtr WavpackOpenFileInput(string infilename, byte[] error, int flags, int normOffset);

        [LibraryImport("wavpackdll", EntryPoint = "WavpackCloseFile")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial IntPtr WavpackCloseFile(IntPtr wpc);

        [LibraryImport("wavpackdll", EntryPoint = "WavpackGetNumChannels")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial int WavpackGetNumChannels(IntPtr wpc);

        [LibraryImport("wavpackdll", EntryPoint = "WavpackGetSampleRate")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial uint WavpackGetSampleRate(IntPtr wpc);

        [LibraryImport("wavpackdll", EntryPoint = "WavpackGetBitsPerSample")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial int WavpackGetBitsPerSample(IntPtr wpc);

        [LibraryImport("wavpackdll", EntryPoint = "WavpackGetNumSamples64")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long WavpackGetNumSamples64(IntPtr wpc);

        [LibraryImport("wavpackdll", EntryPoint = "WavpackGetMode")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial int WavpackGetMode(IntPtr wpc);

        [LibraryImport("wavpackdll", EntryPoint = "WavpackUnpackSamples")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial uint WavpackUnpackSamples(IntPtr wpc, int[] buffer, uint samples);

        [LibraryImport("wavpackdll", EntryPoint = "WavpackGetErrorMessage")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial IntPtr WavpackGetErrorMessage(IntPtr wpc);

        [LibraryImport("wavpackdll", EntryPoint = "WavpackOpenFileOutput")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static unsafe partial IntPtr WavpackOpenFileOutput(
            delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, int> blockOutput,
            IntPtr wvId,
            IntPtr wvcId);

        [LibraryImport("wavpackdll", EntryPoint = "WavpackSetConfiguration64")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial int WavpackSetConfiguration64(IntPtr wpc, ref WavpackConfig config, long totalSamples, IntPtr chanIds);

        [LibraryImport("wavpackdll", EntryPoint = "WavpackPackInit")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial int WavpackPackInit(IntPtr wpc);

        [LibraryImport("wavpackdll", EntryPoint = "WavpackPackSamples")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial int WavpackPackSamples(IntPtr wpc, int[] sampleBuffer, uint sampleCount);

        [LibraryImport("wavpackdll", EntryPoint = "WavpackFlushSamples")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial int WavpackFlushSamples(IntPtr wpc);
    }
}
