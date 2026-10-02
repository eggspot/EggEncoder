using EggEncoder.Codecs.Wav;

namespace EggEncoder.Codecs.Alac
{
    public static class AlacEncoder
    {
        private const int FramesPerBlock = 4096;

        public static void Encode(string sourceWavFilePath, string destAlacFilePath)
        {
            using var wavReader = WavReader.Open(sourceWavFilePath);
            using var session = AlacEncoderSession.OpenSession(destAlacFilePath, wavReader.Channels, wavReader.SampleRate, wavReader.BitsPerSample);

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
