using EggEncoder.Codecs;
using EggEncoder.Codecs.Wav;
using EggEncoder.Native;

namespace EggEncoder.Codecs.Flac
{
    public static class FlacEncoder
    {
        private const int FramesPerBlock = 4096;
        public const uint DefaultCompressionLevel = 5;

        public static void Encode(string sourceWavFilePath, string destFlacFilePath, uint compressionLevel = DefaultCompressionLevel)
        {
            using var wavReader = WavReader.Open(sourceWavFilePath);
            using var session = OpenSession(destFlacFilePath, wavReader.Channels, wavReader.BitsPerSample, wavReader.SampleRate, compressionLevel);

            var interleavedBuffer = new int[FramesPerBlock * wavReader.Channels];

            int framesRead;
            while ((framesRead = wavReader.ReadInterleavedSamples(interleavedBuffer, FramesPerBlock)) > 0)
            {
                session.WriteInterleavedSamples(interleavedBuffer, framesRead);
            }

            session.Finish();
        }

        public static FlacEncoderSession OpenSession(string destFlacFilePath, int channels, int bitsPerSample, int sampleRate, uint compressionLevel = DefaultCompressionLevel)
        {
            var encoder = FlacNative.StreamEncoderNew();
            if (encoder == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to create FLAC stream encoder");
            }

            try
            {
                if (FlacNative.StreamEncoderSetChannels(encoder, (uint)channels) == 0
                    || FlacNative.StreamEncoderSetBitsPerSample(encoder, (uint)bitsPerSample) == 0
                    || FlacNative.StreamEncoderSetSampleRate(encoder, (uint)sampleRate) == 0
                    || FlacNative.StreamEncoderSetCompressionLevel(encoder, compressionLevel) == 0)
                {
                    throw new InvalidOperationException($"Failed to configure FLAC encoder for '{destFlacFilePath}'");
                }

                var initStatus = FlacNative.StreamEncoderInitFile(encoder, destFlacFilePath, IntPtr.Zero, IntPtr.Zero);
                if (initStatus != FlacStreamEncoderInitStatus.Ok)
                {
                    throw new InvalidOperationException($"Failed to initialize FLAC encoder for '{destFlacFilePath}': {initStatus}");
                }

                return new FlacEncoderSession(encoder, destFlacFilePath);
            }
            catch
            {
                FlacNative.StreamEncoderDelete(encoder);
                throw;
            }
        }
    }

    public sealed class FlacEncoderSession : IAudioSink
    {
        private readonly IntPtr _encoder;
        private readonly string _destFlacFilePath;

        private bool _disposed;

        internal FlacEncoderSession(IntPtr encoder, string destFlacFilePath)
        {
            _encoder = encoder;
            _destFlacFilePath = destFlacFilePath;
        }

        public void WriteInterleavedSamples(int[] buffer, int frameCount)
        {
            if (frameCount <= 0)
            {
                return;
            }

            if (FlacNative.StreamEncoderProcessInterleaved(_encoder, buffer, (uint)frameCount) == 0)
            {
                throw new InvalidOperationException($"FLAC encoder failed to process samples for '{_destFlacFilePath}'");
            }
        }

        public void Finish()
        {
            if (FlacNative.StreamEncoderFinish(_encoder) == 0)
            {
                throw new InvalidOperationException($"FLAC encoder failed to finish writing '{_destFlacFilePath}'");
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            FlacNative.StreamEncoderDelete(_encoder);
        }
    }
}
