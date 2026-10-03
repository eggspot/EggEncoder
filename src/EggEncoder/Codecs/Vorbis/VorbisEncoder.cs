using EggEncoder.Codecs.Wav;

namespace EggEncoder.Codecs.Vorbis
{
    public static class VorbisEncoder
    {
        private const int FramesPerBlock = 4096;

        public static void Encode(string sourceWavFilePath, string destOggFilePath, float quality = VorbisEncoderSession.DefaultQuality)
        {
            using var wavReader = WavReader.Open(sourceWavFilePath);
            using var session = VorbisEncoderSession.OpenSession(destOggFilePath, wavReader.Channels, wavReader.SampleRate, wavReader.BitsPerSample, quality);

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
