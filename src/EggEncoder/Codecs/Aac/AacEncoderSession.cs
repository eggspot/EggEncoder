namespace EggEncoder.Codecs.Aac
{
    public sealed class AacEncoderSession : IAudioSink
    {
        private readonly string _destFilePath;
        private readonly int _channels;
        private readonly int _sampleRate;
        private readonly List<short> _samples = [];

        private AacEncoderSession(string destFilePath, int channels, int sampleRate)
        {
            _destFilePath = destFilePath ?? throw new ArgumentNullException(nameof(destFilePath));
            _channels = channels;
            _sampleRate = sampleRate;
        }

        public static AacEncoderSession OpenSession(string destFilePath, int channels, int sampleRate)
        {
            if (channels != 1)
            {
                throw new NotSupportedException("Only mono AAC encoding is supported");
            }

            return new AacEncoderSession(destFilePath, channels, sampleRate);
        }

        public void WriteInterleavedSamples(int[] buffer, int frameCount)
        {
            for (var i = 0; i < frameCount * _channels; i++)
            {
                _samples.Add((short)buffer[i]);
            }
        }

        public void Finish()
        {
            AacEncoder.Encode(_destFilePath, _samples, _channels, _sampleRate);
        }

        public void Dispose()
        {
        }
    }
}
