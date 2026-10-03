using EggEncoder.Codecs.Wav;

namespace EggEncoder.Codecs.Opus
{
    public static class OpusEncoder
    {
        private const int FramesPerBlock = 4096;

        public static void Encode(string sourceWavFilePath, string destOpusFilePath)
        {
            using var wavReader = WavReader.Open(sourceWavFilePath);
            using var session = OpusEncoderSession.OpenSession(destOpusFilePath, wavReader.Channels, wavReader.SampleRate, wavReader.BitsPerSample);

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
