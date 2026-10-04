using System.Text;
using EggEncoder.Native;

namespace EggEncoder.Codecs.WavPack
{
    // Decodes a WavPack (.wv) file via libwavpack -- the official project's own native C library
    // (NuGet has no pure-managed WavPack decoder, unlike NLayer/Concentus/NVorbis for MP3/Opus/Vorbis),
    // loaded the same way libFLAC/libmp3lame already are (NativeLibraryLoader, win-x64 only). The
    // bundled wavpackdll.dll is the official project's own prebuilt binary, downloaded from its GitHub
    // release and checksum-verified against its own published sums.txt -- not a third-party rebuild.
    //
    // Scoped to mono/stereo, 16/24-bit lossless integer PCM, matching every other codec's convention
    // in this project -- WavPack itself also supports more channels, floating-point samples, and a
    // lossy/hybrid mode, all rejected here rather than silently mishandled.
    //
    // WavpackUnpackSamples's int32 buffer convention already matches this project's own native
    // per-bit-depth-range int PCM convention exactly (e.g. -32768..32767 for 16-bit, confirmed by
    // reading WavPack's own reference CLI source, not assumed) -- no scaling needed on decode, unlike
    // Vorbis's normalized-float convention.
    public static class WavPackDecoder
    {
        private const int ReadBufferFrames = 4096;

        public static WavPackStreamInfo Decode(string filePath, AudioBlockDecodedCallback onBlockDecoded)
        {
            var error = new byte[80];
            var wpc = WavPackNative.WavpackOpenFileInput(filePath, error, WavPackNative.OpenFileUtf8, 0);
            if (wpc == IntPtr.Zero)
            {
                throw new InvalidDataException($"Failed to open '{filePath}' as WavPack: {DecodeErrorBuffer(error)}");
            }

            try
            {
                var channels = WavPackNative.WavpackGetNumChannels(wpc);
                if (channels is not 1 and not 2)
                {
                    throw new NotSupportedException($"'{filePath}' has {channels} channels; only mono and stereo WavPack are supported");
                }

                var bitsPerSample = WavPackNative.WavpackGetBitsPerSample(wpc);
                if (bitsPerSample != 16 && bitsPerSample != 24)
                {
                    throw new NotSupportedException($"'{filePath}' has {bitsPerSample}-bit samples; only 16-bit and 24-bit WavPack are supported");
                }

                var mode = WavPackNative.WavpackGetMode(wpc);
                if ((mode & WavPackNative.ModeLossless) == 0 || (mode & WavPackNative.ModeFloat) != 0)
                {
                    throw new NotSupportedException($"'{filePath}' is not lossless integer WavPack; lossy/hybrid and floating-point WavPack are not supported");
                }

                var sampleRate = (int)WavPackNative.WavpackGetSampleRate(wpc);
                var totalSamples = WavPackNative.WavpackGetNumSamples64(wpc);

                var buffer = new int[ReadBufferFrames * channels];

                uint framesRead;
                while ((framesRead = WavPackNative.WavpackUnpackSamples(wpc, buffer, ReadBufferFrames)) > 0)
                {
                    onBlockDecoded(new ReadOnlySpan<int>(buffer, 0, (int)framesRead * channels), channels, sampleRate, bitsPerSample, totalSamples);
                }

                return new WavPackStreamInfo
                {
                    Channels = channels,
                    SampleRate = sampleRate,
                    BitsPerSample = bitsPerSample,
                    TotalSamples = totalSamples
                };
            }
            finally
            {
                WavPackNative.WavpackCloseFile(wpc);
            }
        }

        private static string DecodeErrorBuffer(byte[] error)
        {
            var nullIndex = Array.IndexOf(error, (byte)0);
            var length = nullIndex < 0 ? error.Length : nullIndex;
            return Encoding.UTF8.GetString(error, 0, length);
        }
    }

    public class WavPackStreamInfo
    {
        public required int Channels { get; init; }

        public required int SampleRate { get; init; }

        public required int BitsPerSample { get; init; }

        public required long TotalSamples { get; init; }
    }
}
