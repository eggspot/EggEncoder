namespace EggEncoder.Codecs
{
    public delegate void AudioBlockDecodedCallback(ReadOnlySpan<int> block, int channels, int sampleRate, int bitsPerSample, long totalSamplesPerChannel);
}
