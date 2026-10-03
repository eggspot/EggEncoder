using EggEncoder.Codecs.Wav;

namespace EggEncoder.Codecs.Tta
{
    public static class TtaEncoder
    {
        private const int FramesPerBlock = 4096;

        public static void Encode(string sourceWavFilePath, string destTtaFilePath)
        {
            using var wavReader = WavReader.Open(sourceWavFilePath);
            using var session = TtaEncoderSession.OpenSession(destTtaFilePath, wavReader.Channels, wavReader.SampleRate, wavReader.BitsPerSample);

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
