using EggEncoder.Codecs.Wav;

namespace EggEncoder.Codecs.Opus
{
    public static class OpusEncoder
    {
        private const int FramesPerBlock = 4096;

        public static void Encode(string sourceWavFilePath, string destOpusFilePath, IReadOnlyDictionary<string, string>? tags = null)
        {
            using var wavReader = WavReader.Open(sourceWavFilePath);
            using var session = OpusEncoderSession.OpenSession(destOpusFilePath, wavReader.Channels, wavReader.SampleRate, wavReader.BitsPerSample, tags);

            var interleavedBuffer = new int[FramesPerBlock * wavReader.Channels];

            int framesRead;
            while ((framesRead = wavReader.ReadInterleavedSamples(interleavedBuffer, FramesPerBlock)) > 0)
            {
                session.WriteInterleavedSamples(interleavedBuffer, framesRead);
            }

            session.Finish();
        }
    }
}
