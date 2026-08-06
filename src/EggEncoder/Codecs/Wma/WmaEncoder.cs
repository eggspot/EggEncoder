namespace EggEncoder.Codecs.Wma
{
    public static class WmaEncoder
    {
        public static void Encode(string destFilePath, IReadOnlyList<short> interleavedSamples, int channels, int sampleRate)
        {
            using var session = WmaEncoderSession.OpenSession(destFilePath, channels, sampleRate);

            var buffer = new int[interleavedSamples.Count];
            for (var i = 0; i < interleavedSamples.Count; i++)
            {
                buffer[i] = interleavedSamples[i];
            }

            session.WriteInterleavedSamples(buffer, channels > 0 ? buffer.Length / channels : 0);
            session.Finish();
        }
    }
}
