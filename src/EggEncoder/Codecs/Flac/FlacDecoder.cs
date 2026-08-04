using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EggEncoder.Native;

namespace EggEncoder.Codecs.Flac
{
    public static class FlacDecoder
    {
        public static unsafe FlacStreamInfo Decode(string flacFilePath, AudioBlockDecodedCallback onBlockDecoded)
        {
            var decoder = FlacNative.StreamDecoderNew();
            if (decoder == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to create FLAC stream decoder");
            }

            try
            {
                var state = new DecodeState(flacFilePath, onBlockDecoded);
                var stateHandle = GCHandle.Alloc(state);

                try
                {
                    var initStatus = FlacNative.StreamDecoderInitFile(decoder, flacFilePath, &WriteCallback, null, &ErrorCallback, GCHandle.ToIntPtr(stateHandle));
                    if (initStatus != FlacStreamDecoderInitStatus.Ok)
                    {
                        throw new InvalidOperationException($"Failed to initialize FLAC decoder for '{flacFilePath}': {initStatus}");
                    }

                    if (FlacNative.StreamDecoderProcessUntilEndOfStream(decoder) == 0 || state.CallbackException is not null)
                    {
                        throw new InvalidOperationException($"Failed to decode '{flacFilePath}'", state.CallbackException);
                    }
                }
                finally
                {
                    stateHandle.Free();
                }

                FlacNative.StreamDecoderFinish(decoder);

                return new FlacStreamInfo
                {
                    Channels = state.Channels,
                    SampleRate = state.SampleRate,
                    BitsPerSample = state.BitsPerSample,
                    TotalSamples = state.TotalSamples
                };
            }
            finally
            {
                FlacNative.StreamDecoderDelete(decoder);
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static FlacStreamDecoderWriteStatus WriteCallback(IntPtr decoderHandle, IntPtr frame, IntPtr buffer, IntPtr clientData)
        {
            var state = (DecodeState)GCHandle.FromIntPtr(clientData).Target!;

            try
            {
                if (!state.FormatKnown)
                {
                    state.Channels = (int)FlacNative.StreamDecoderGetChannels(decoderHandle);
                    state.SampleRate = (int)FlacNative.StreamDecoderGetSampleRate(decoderHandle);
                    state.BitsPerSample = (int)FlacNative.StreamDecoderGetBitsPerSample(decoderHandle);
                    state.TotalSamples = (long)FlacNative.StreamDecoderGetTotalSamples(decoderHandle);
                    state.FormatKnown = true;
                }

                var blockSize = Marshal.ReadInt32(frame);
                var requiredLength = blockSize * state.Channels;

                if (state.InterleavedBlockBuffer.Length < requiredLength)
                {
                    state.InterleavedBlockBuffer = new int[requiredLength];
                }

                for (var sampleIndex = 0; sampleIndex < blockSize; sampleIndex++)
                {
                    for (var channel = 0; channel < state.Channels; channel++)
                    {
                        var channelBufferPointer = Marshal.ReadIntPtr(buffer, channel * IntPtr.Size);
                        state.InterleavedBlockBuffer[(sampleIndex * state.Channels) + channel] = Marshal.ReadInt32(channelBufferPointer, sampleIndex * sizeof(int));
                    }
                }

                state.OnBlockDecoded(new ReadOnlySpan<int>(state.InterleavedBlockBuffer, 0, requiredLength), state.Channels, state.SampleRate, state.BitsPerSample, state.TotalSamples);

                return FlacStreamDecoderWriteStatus.Continue;
            }
            catch (Exception e)
            {
                state.CallbackException = e;
                return FlacStreamDecoderWriteStatus.Abort;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void ErrorCallback(IntPtr decoderHandle, int status, IntPtr clientData)
        {
            var state = (DecodeState)GCHandle.FromIntPtr(clientData).Target!;

            state.CallbackException ??= new InvalidOperationException($"FLAC decoder error while reading '{state.FlacFilePath}': status {status}");
        }

        private sealed class DecodeState(string flacFilePath, AudioBlockDecodedCallback onBlockDecoded)
        {
            public string FlacFilePath { get; } = flacFilePath;

            public AudioBlockDecodedCallback OnBlockDecoded { get; } = onBlockDecoded;

            public int Channels { get; set; }

            public int SampleRate { get; set; }

            public int BitsPerSample { get; set; }

            public long TotalSamples { get; set; }

            public bool FormatKnown { get; set; }

            public int[] InterleavedBlockBuffer { get; set; } = [];

            public Exception? CallbackException { get; set; }
        }
    }

    public class FlacStreamInfo
    {
        public required int Channels { get; init; }

        public required int SampleRate { get; init; }

        public required int BitsPerSample { get; init; }

        public required long TotalSamples { get; init; }
    }
}
