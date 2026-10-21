using EggEncoder.Pcm;

namespace EggEncoder.Codecs.Aac
{
    public sealed class AacEncoderSession : IAudioSink
    {
        private readonly Stream _destStream;
        private readonly bool _leaveOpen;
        private readonly AacFrameEncoder _frameEncoder;
        private readonly int _channels;
        private readonly int _bitsPerSample;

        private bool _disposed;

        private AacEncoderSession(Stream destStream, bool leaveOpen, int channels, int sampleRate, int bitsPerSample)
        {
            _destStream = destStream;
            _leaveOpen = leaveOpen;
            _channels = channels;
            _bitsPerSample = bitsPerSample;
            var sampleRateIndex = AacFrameEncoder.ValidateAndGetSampleRateIndex(channels, sampleRate);
            _frameEncoder = new AacFrameEncoder(channels, sampleRate, (payload, _, _) =>
            {
                destStream.Write(AdtsFrameWriter.Wrap(payload, sampleRateIndex, channels));
            });
        }

        public static AacEncoderSession OpenSession(string destFilePath, int channels, int sampleRate) =>
            OpenSession(destFilePath, channels, sampleRate, bitsPerSample: 16);

        /// <summary>
        /// Same as <see cref="OpenSession(string, int, int)"/>, but for input samples in
        /// <paramref name="bitsPerSample"/>'s own native range (8, 16, 24, or 32 -- see
        /// <see cref="BitDepthFormatTransform"/>) rather than always 16-bit. The AAC codec core
        /// only accepts 16-bit-range samples, so anything else is rescaled (rounded, clamped) to
        /// 16-bit as it's written -- without this, a 24-bit source's samples would wrap around the
        /// 16-bit range and encode as noise.
        /// </summary>
        public static AacEncoderSession OpenSession(string destFilePath, int channels, int sampleRate, int bitsPerSample)
        {
            ValidateParameters(channels, sampleRate, bitsPerSample);

            var destStream = File.Create(destFilePath);
            try
            {
                return new AacEncoderSession(destStream, leaveOpen: false, channels, sampleRate, bitsPerSample);
            }
            catch
            {
                destStream.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Creates an AAC destination from an already-open <see cref="Stream"/> instead of a file
        /// path -- for a destination that isn't a local file (an HTTP request body, blob storage,
        /// an in-memory buffer). Needs no <see cref="Stream.CanSeek"/> at all: every ADTS frame is
        /// written forward-only as soon as it's encoded (<see cref="AacFrameEncoder"/>'s own
        /// per-frame callback), the same "no patch-up, ever" shape
        /// <see cref="Tta.TtaEncoderSession"/>'s own <c>Stream</c> overload already established,
        /// just without even that type's own buffer-until-<see cref="Finish"/> step. Does NOT
        /// dispose <paramref name="destStream"/> -- the caller opened it, so the caller is
        /// responsible for disposing it.
        /// </summary>
        public static AacEncoderSession OpenSession(Stream destStream, int channels, int sampleRate) =>
            OpenSession(destStream, channels, sampleRate, bitsPerSample: 16);

        /// <summary>
        /// Same as <see cref="OpenSession(Stream, int, int)"/>, but for input samples in
        /// <paramref name="bitsPerSample"/>'s own native range -- see
        /// <see cref="OpenSession(string, int, int, int)"/>.
        /// </summary>
        public static AacEncoderSession OpenSession(Stream destStream, int channels, int sampleRate, int bitsPerSample)
        {
            ArgumentNullException.ThrowIfNull(destStream);
            ValidateParameters(channels, sampleRate, bitsPerSample);

            return new AacEncoderSession(destStream, leaveOpen: true, channels, sampleRate, bitsPerSample);
        }

        private static void ValidateParameters(int channels, int sampleRate, int bitsPerSample)
        {
            AacFrameEncoder.ValidateAndGetSampleRateIndex(channels, sampleRate);

            if (bitsPerSample is not (8 or 16 or 24 or 32))
            {
                throw new NotSupportedException($"{bitsPerSample}-bit input is not supported for AAC encoding; expected 8, 16, 24, or 32");
            }
        }

        internal long BytesWrittenForTesting => _destStream.Position;

        public void WriteInterleavedSamples(int[] buffer, int frameCount)
        {
            var sampleCount = frameCount * _channels;
            for (var i = 0; i < sampleCount; i++)
            {
                _frameEncoder.WriteSample((short)BitDepthFormatTransform.Rescale(buffer[i], _bitsPerSample, 16));
            }
        }

        public void Finish()
        {
            _frameEncoder.Flush();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _frameEncoder.Dispose();

            if (!_leaveOpen)
            {
                _destStream.Dispose();
            }
        }
    }
}
