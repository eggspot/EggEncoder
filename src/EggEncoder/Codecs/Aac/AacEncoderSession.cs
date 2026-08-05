namespace EggEncoder.Codecs.Aac
{
    public sealed class AacEncoderSession : IAudioSink
    {
        private readonly FileStream _destStream;
        private readonly AacFrameEncoder _frameEncoder;
        private readonly int _channels;

        private bool _disposed;

        private AacEncoderSession(FileStream destStream, int channels, int sampleRate)
        {
            _destStream = destStream;
            _channels = channels;
            _frameEncoder = new AacFrameEncoder(destStream, channels, sampleRate);
        }

        public static AacEncoderSession OpenSession(string destFilePath, int channels, int sampleRate)
        {
            AacFrameEncoder.ValidateAndGetSampleRateIndex(channels, sampleRate);

            var destStream = File.Create(destFilePath);
            try
            {
                return new AacEncoderSession(destStream, channels, sampleRate);
            }
            catch
            {
                destStream.Dispose();
                throw;
            }
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
            _destStream.Dispose();
        }
    }
}
