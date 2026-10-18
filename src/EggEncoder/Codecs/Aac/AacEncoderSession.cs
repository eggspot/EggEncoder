namespace EggEncoder.Codecs.Aac
{
    public sealed class AacEncoderSession : IAudioSink
    {
        private readonly Stream _destStream;
        private readonly bool _leaveOpen;
        private readonly AacFrameEncoder _frameEncoder;
        private readonly int _channels;

        private bool _disposed;

        private AacEncoderSession(Stream destStream, bool leaveOpen, int channels, int sampleRate)
        {
            _destStream = destStream;
            _leaveOpen = leaveOpen;
            _channels = channels;
            var sampleRateIndex = AacFrameEncoder.ValidateAndGetSampleRateIndex(channels, sampleRate);
            _frameEncoder = new AacFrameEncoder(channels, sampleRate, (payload, _, _) =>
            {
                destStream.Write(AdtsFrameWriter.Wrap(payload, sampleRateIndex, channels));
            });
        }

        public static AacEncoderSession OpenSession(string destFilePath, int channels, int sampleRate)
        {
            AacFrameEncoder.ValidateAndGetSampleRateIndex(channels, sampleRate);

            var destStream = File.Create(destFilePath);
            try
            {
                return new AacEncoderSession(destStream, leaveOpen: false, channels, sampleRate);
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
        public static AacEncoderSession OpenSession(Stream destStream, int channels, int sampleRate)
        {
            ArgumentNullException.ThrowIfNull(destStream);
            AacFrameEncoder.ValidateAndGetSampleRateIndex(channels, sampleRate);

            return new AacEncoderSession(destStream, leaveOpen: true, channels, sampleRate);
        }

        internal long BytesWrittenForTesting => _destStream.Position;

        public void WriteInterleavedSamples(int[] buffer, int frameCount)
        {
            var sampleCount = frameCount * _channels;
            for (var i = 0; i < sampleCount; i++)
            {
                _frameEncoder.WriteSample((short)buffer[i]);
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
