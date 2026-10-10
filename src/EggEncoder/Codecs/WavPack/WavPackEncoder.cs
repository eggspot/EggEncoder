using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EggEncoder.Codecs.Wav;
using EggEncoder.Native;

namespace EggEncoder.Codecs.WavPack
{
    // Encodes mono/stereo 16/24-bit lossless integer PCM into a WavPack (.wv) file via libwavpack --
    // see WavPackDecoder's own doc comment for the native-binary provenance/scoping rationale, which
    // applies equally here.
    //
    // Unlike libFLAC's own encoder (FlacNative.StreamEncoderInitFile writes directly to a file path,
    // no custom callback needed), WavpackOpenFileOutput has no file-path convenience entry point at
    // all -- it only ever writes through a caller-supplied block-output callback, so this follows
    // the same [UnmanagedCallersOnly] + GCHandle pattern (not a marshaled delegate closure, which
    // isn't AOT-safe) the old native FlacDecoder's own decode callback used to, before it was
    // replaced by a pure managed decoder.
    //
    // WavpackSetConfiguration64 accepts total_samples == -1 for "unknown, streaming" (confirmed from
    // WavPack's own reference CLI, which uses exactly this for stdin input) and a WavpackUpdateNumSamples
    // function to patch the written file's first block once the true count becomes known -- but
    // OpenSession here always requires an exact total up front instead, the same requirement
    // WavWriter.Create/AiffWriter.Create already have, so AudioCutter.Pipeline.cs's existing generic
    // DeferredFixedHeaderSink (built for exactly this "defer until the true count is known" need) can
    // be reused unchanged rather than this project needing to reimplement that native seek-and-patch
    // sequence itself.
    //
    // Unlike FLAC/TTA/Opus/Vorbis, WavPack genuinely cannot represent an empty/zero-sample stream --
    // confirmed from WavPack's own reference CLI (cli/wavpack.c), which refuses to encode one outright
    // ("no raw PCM data to encode!"), and independently reconfirmed via a real CI failure here:
    // WavpackSetConfiguration64 itself rejects total_samples == 0 ("invalid total sample count!"), and
    // substituting -1 ("unknown") instead produces a file that WavpackOpenFileInput then refuses to
    // read back ("can't read all of WavPack file!") since no data block is ever flushed for it to find.
    // OpenSession rejects totalSamples <= 0 outright rather than letting either failure surface
    // confusingly later.
    public static class WavPackEncoder
    {
        private const int FramesPerBlock = 4096;

        public static void Encode(string sourceWavFilePath, string destWvFilePath)
        {
            using var wavReader = WavReader.Open(sourceWavFilePath);
            using var session = WavPackEncoderSession.OpenSession(destWvFilePath, wavReader.Channels, wavReader.BitsPerSample, wavReader.SampleRate, wavReader.TotalSamples);

            var interleavedBuffer = new int[FramesPerBlock * wavReader.Channels];

            int framesRead;
            while ((framesRead = wavReader.ReadInterleavedSamples(interleavedBuffer, FramesPerBlock)) > 0)
            {
                session.WriteInterleavedSamples(interleavedBuffer, framesRead);
            }

            session.Finish();
        }
    }

    public sealed class WavPackEncoderSession : IAudioSink
    {
        private readonly FileStream _destStream;
        private readonly IntPtr _wpc;
        private readonly GCHandle _stateHandle;
        private readonly EncodeState _state;

        private bool _disposed;

        private WavPackEncoderSession(FileStream destStream, IntPtr wpc, GCHandle stateHandle, EncodeState state)
        {
            _destStream = destStream;
            _wpc = wpc;
            _stateHandle = stateHandle;
            _state = state;
        }

        public static WavPackEncoderSession OpenSession(string destFilePath, int channels, int bitsPerSample, int sampleRate, long totalSamples)
        {
            if (channels is not 1 and not 2)
            {
                throw new NotSupportedException($"'{destFilePath}' requests {channels} channels; only mono and stereo WavPack encoding is supported");
            }

            if (bitsPerSample != 16 && bitsPerSample != 24)
            {
                throw new NotSupportedException($"'{destFilePath}' requests {bitsPerSample}-bit samples; only 16-bit and 24-bit WavPack encoding is supported");
            }

            if (sampleRate <= 0)
            {
                throw new NotSupportedException($"'{destFilePath}' requests a sample rate of {sampleRate}; only positive sample rates are supported for WavPack encoding");
            }

            if (totalSamples <= 0)
            {
                // See this class's own doc comment above for why -- WavPack genuinely cannot
                // represent an empty/zero-sample stream, confirmed from its own reference CLI.
                throw new NotSupportedException($"'{destFilePath}' requests {totalSamples} total samples; WavPack cannot encode an empty/zero-sample stream");
            }

            var destStream = File.Create(destFilePath);
            var state = new EncodeState(destStream);
            var stateHandle = GCHandle.Alloc(state);
            var wpc = IntPtr.Zero;

            try
            {
                unsafe
                {
                    wpc = WavPackNative.WavpackOpenFileOutput(&WriteBlockCallback, GCHandle.ToIntPtr(stateHandle), IntPtr.Zero);
                }

                if (wpc == IntPtr.Zero)
                {
                    throw new InvalidOperationException($"Failed to create WavPack encoder context for '{destFilePath}'");
                }

                var config = new WavpackConfig
                {
                    NumChannels = channels,
                    SampleRate = sampleRate,
                    BitsPerSample = bitsPerSample,
                    BytesPerSample = bitsPerSample / 8
                };

                if (WavPackNative.WavpackSetConfiguration64(wpc, ref config, totalSamples, IntPtr.Zero) == 0)
                {
                    throw new InvalidOperationException($"Failed to configure WavPack encoder for '{destFilePath}': {GetErrorMessage(wpc)}");
                }

                if (WavPackNative.WavpackPackInit(wpc) == 0)
                {
                    throw new InvalidOperationException($"Failed to initialize WavPack encoder for '{destFilePath}': {GetErrorMessage(wpc)}");
                }

                return new WavPackEncoderSession(destStream, wpc, stateHandle, state);
            }
            catch
            {
                if (wpc != IntPtr.Zero)
                {
                    WavPackNative.WavpackCloseFile(wpc);
                }

                stateHandle.Free();
                destStream.Dispose();
                throw;
            }
        }

        public void WriteInterleavedSamples(int[] buffer, int frameCount)
        {
            if (frameCount <= 0)
            {
                return;
            }

            var success = WavPackNative.WavpackPackSamples(_wpc, buffer, (uint)frameCount) != 0;
            ThrowIfCallbackFailed();

            if (!success)
            {
                throw new InvalidOperationException($"WavPack encoder failed to process samples: {GetErrorMessage(_wpc)}");
            }
        }

        public void Finish()
        {
            var success = WavPackNative.WavpackFlushSamples(_wpc) != 0;
            ThrowIfCallbackFailed();

            if (!success)
            {
                throw new InvalidOperationException($"WavPack encoder failed to finish writing: {GetErrorMessage(_wpc)}");
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            WavPackNative.WavpackCloseFile(_wpc);
            _stateHandle.Free();
            _destStream.Dispose();
        }

        private void ThrowIfCallbackFailed()
        {
            if (_state.CallbackException is { } exception)
            {
                throw new InvalidOperationException("WavPack encoder's write callback failed", exception);
            }
        }

        private static string GetErrorMessage(IntPtr wpc)
        {
            var pointer = WavPackNative.WavpackGetErrorMessage(wpc);
            return pointer == IntPtr.Zero ? "unknown error" : Marshal.PtrToStringUTF8(pointer) ?? "unknown error";
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static unsafe int WriteBlockCallback(IntPtr id, IntPtr data, int bcount)
        {
            var state = (EncodeState)GCHandle.FromIntPtr(id).Target!;

            try
            {
                var span = new ReadOnlySpan<byte>((void*)data, bcount);
                state.DestStream.Write(span);
                return 1;
            }
            catch (Exception e)
            {
                state.CallbackException = e;
                return 0;
            }
        }

        private sealed class EncodeState(FileStream destStream)
        {
            public FileStream DestStream { get; } = destStream;

            public Exception? CallbackException { get; set; }
        }
    }
}
