using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EggEncoder.Native
{
    internal enum BeMp3Mode
    {
        Stereo = 0,
        JointStereo,
        DualChannel,
        Mono,
    }

    internal enum BeQualityPreset
    {
        NoPreset = -1,
    }

    internal enum BeVbrMethod
    {
        None = -1,
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal unsafe struct BeConfig
    {
        public const uint ConfigLame = 256;

        public uint DwConfig;
        public uint DwStructVersion;
        public uint DwStructSize;
        public uint DwSampleRate;
        public uint DwReSampleRate;
        public int NMode;
        public uint DwBitrate;
        public uint DwMaxBitrate;
        public int NPreset;
        public uint DwMpegVersion;
        public uint DwPsyModel;
        public uint DwEmphasis;
        public int BPrivate;
        public int BCrc;
        public int BCopyright;
        public int BOriginal;
        public int BWriteVbrHeader;
        public int BEnableVbr;
        public int NVbrQuality;
        public uint DwVbrAbrBps;
        public int NVbrMethod;
        public int BNoRes;
        public int BStrictIso;
        public ushort NQuality;

        // A fixed buffer (rather than byte[]) keeps the whole struct blittable, which is what
        // lets LibraryImport marshal it automatically — byte[] needed MarshalAs(ByValArray),
        // which LibraryImport's generator does not support (SYSLIB1051).
        public fixed byte BtReserved[237];
    }

    internal static partial class Mp3Native
    {
        [LibraryImport("libmp3lame", EntryPoint = "beInitStream")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial uint BeInitStream(ref BeConfig config, ref uint samples, out uint bufferSize, out IntPtr streamHandle);

        [LibraryImport("libmp3lame", EntryPoint = "beEncodeChunk")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial uint BeEncodeChunk(IntPtr streamHandle, uint sampleCount, short[] samples, byte[] output, out uint bytesWritten);

        [LibraryImport("libmp3lame", EntryPoint = "beDeinitStream")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial uint BeDeinitStream(IntPtr streamHandle, byte[] output, out uint bytesWritten);

        [LibraryImport("libmp3lame", EntryPoint = "beCloseStream")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial uint BeCloseStream(IntPtr streamHandle);
    }
}
