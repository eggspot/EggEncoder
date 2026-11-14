using EggEncoder.Transform;

namespace EggEncoder.Codecs.Aac
{
    public static class AacEncoder
    {
        public static void Encode(string outputFilePath, IReadOnlyList<short> pcmSamples, int channels, int sampleRate) =>
            Encode(outputFilePath, pcmSamples, channels, sampleRate, bitRate: 0);

        /// <summary>
        /// Encodes interleaved 16-bit PCM (mono or stereo) as an ADTS AAC-LC file at a target bit
        /// rate for all channels together, in bits per second; 0 means 96 kb/s per channel.
        /// </summary>
        public static void Encode(string outputFilePath, IReadOnlyList<short> pcmSamples, int channels, int sampleRate, int bitRate)
        {
            var sampleRateIndex = AacFrameEncoder.ValidateAndGetSampleRateIndex(channels, sampleRate);
            ArgumentOutOfRangeException.ThrowIfNegative(bitRate);

            using var outputStream = File.Create(outputFilePath);
            using var frameEncoder = new AacFrameEncoder(channels, sampleRate, (payload, _, _) =>
            {
                outputStream.Write(AdtsFrameWriter.Wrap(payload, sampleRateIndex, channels));
            }, bitRate);

            foreach (var sample in pcmSamples)
            {
                frameEncoder.WriteSample(sample);
            }

            frameEncoder.Flush();
        }
    }
}
