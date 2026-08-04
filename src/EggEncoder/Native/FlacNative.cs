using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EggEncoder.Native
{
    internal enum FlacStreamEncoderInitStatus
    {
        Ok = 0,
        EncoderError,
        UnsupportedContainer,
        InvalidCallbacks,
        InvalidNumberOfChannels,
        InvalidBitsPerSample,
        InvalidSampleRate,
        InvalidBlockSize,
        InvalidMaxLpcOrder,
        InvalidQlpCoeffPrecision,
        BlockSizeTooSmallForLpcOrder,
        NotStreamable,
        InvalidMetadata,
        AlreadyInitialized,
    }

    internal enum FlacStreamDecoderInitStatus
    {
        Ok = 0,
        UnsupportedContainer,
        InvalidCallbacks,
        MemoryAllocationError,
        ErrorOpeningFile,
        AlreadyInitialized,
    }

    internal enum FlacStreamDecoderWriteStatus
    {
        Continue = 0,
        Abort,
    }

    internal static partial class FlacNative
    {
        [LibraryImport("libFLAC", EntryPoint = "FLAC__stream_encoder_new")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial IntPtr StreamEncoderNew();

        [LibraryImport("libFLAC", EntryPoint = "FLAC__stream_encoder_delete")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial void StreamEncoderDelete(IntPtr encoder);

        [LibraryImport("libFLAC", EntryPoint = "FLAC__stream_encoder_set_channels")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial int StreamEncoderSetChannels(IntPtr encoder, uint value);

        [LibraryImport("libFLAC", EntryPoint = "FLAC__stream_encoder_set_bits_per_sample")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial int StreamEncoderSetBitsPerSample(IntPtr encoder, uint value);

        [LibraryImport("libFLAC", EntryPoint = "FLAC__stream_encoder_set_sample_rate")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial int StreamEncoderSetSampleRate(IntPtr encoder, uint value);

        [LibraryImport("libFLAC", EntryPoint = "FLAC__stream_encoder_set_compression_level")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial int StreamEncoderSetCompressionLevel(IntPtr encoder, uint value);

        [LibraryImport("libFLAC", EntryPoint = "FLAC__stream_encoder_init_file")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial FlacStreamEncoderInitStatus StreamEncoderInitFile(
            IntPtr encoder,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string filename,
            IntPtr progressCallback,
            IntPtr clientData);

        [LibraryImport("libFLAC", EntryPoint = "FLAC__stream_encoder_process_interleaved")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial int StreamEncoderProcessInterleaved(IntPtr encoder, int[] buffer, uint samples);

        [LibraryImport("libFLAC", EntryPoint = "FLAC__stream_encoder_finish")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial int StreamEncoderFinish(IntPtr encoder);

        [LibraryImport("libFLAC", EntryPoint = "FLAC__stream_decoder_new")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial IntPtr StreamDecoderNew();

        [LibraryImport("libFLAC", EntryPoint = "FLAC__stream_decoder_delete")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial void StreamDecoderDelete(IntPtr decoder);

        [LibraryImport("libFLAC", EntryPoint = "FLAC__stream_decoder_init_file")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static unsafe partial FlacStreamDecoderInitStatus StreamDecoderInitFile(
            IntPtr decoder,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string filename,
            delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, FlacStreamDecoderWriteStatus> writeCallback,
            delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void> metadataCallback,
            delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, void> errorCallback,
            IntPtr clientData);

        [LibraryImport("libFLAC", EntryPoint = "FLAC__stream_decoder_process_until_end_of_stream")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial int StreamDecoderProcessUntilEndOfStream(IntPtr decoder);

        [LibraryImport("libFLAC", EntryPoint = "FLAC__stream_decoder_finish")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial int StreamDecoderFinish(IntPtr decoder);

        [LibraryImport("libFLAC", EntryPoint = "FLAC__stream_decoder_get_channels")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial uint StreamDecoderGetChannels(IntPtr decoder);

        [LibraryImport("libFLAC", EntryPoint = "FLAC__stream_decoder_get_bits_per_sample")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial uint StreamDecoderGetBitsPerSample(IntPtr decoder);

        [LibraryImport("libFLAC", EntryPoint = "FLAC__stream_decoder_get_sample_rate")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial uint StreamDecoderGetSampleRate(IntPtr decoder);

        [LibraryImport("libFLAC", EntryPoint = "FLAC__stream_decoder_get_total_samples")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial ulong StreamDecoderGetTotalSamples(IntPtr decoder);
    }
}
