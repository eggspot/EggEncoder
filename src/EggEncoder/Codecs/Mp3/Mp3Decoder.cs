using EggEncoder.Codecs;
using NLayer;

namespace EggEncoder.Codecs.Mp3
{
    public static class Mp3Decoder
    {
        private const int BytesPerSample = 2;
        private const int BitsPerSample = 16;
        private const int FramesPerBlock = 4096;

        public static Mp3StreamInfo Decode(string mp3FilePath, AudioBlockDecodedCallback onBlockDecoded)
        {
            using var mpegFile = new MpegFile(mp3FilePath);
            var channels = mpegFile.Channels;
            var sampleRate = mpegFile.SampleRate;
            var totalSamplesPerChannel = (long)(mpegFile.Duration.TotalSeconds * sampleRate);

            var byteBuffer = new byte[FramesPerBlock * channels * BytesPerSample];
            var interleavedBuffer = new int[FramesPerBlock * channels];

            int bytesRead;
            while ((bytesRead = mpegFile.ReadSamplesInt16(byteBuffer, 0, byteBuffer.Length)) > 0)
            {
                var sampleCount = bytesRead / BytesPerSample;
                for (var i = 0; i < sampleCount; i++)
                {
                    interleavedBuffer[i] = (short)(byteBuffer[i * 2] | (byteBuffer[(i * 2) + 1] << 8));
                }

                onBlockDecoded(new ReadOnlySpan<int>(interleavedBuffer, 0, sampleCount), channels, sampleRate, BitsPerSample, totalSamplesPerChannel);
            }

            return new Mp3StreamInfo
            {
                Channels = channels,
                SampleRate = sampleRate,
                BitsPerSample = BitsPerSample,
                TotalSamples = totalSamplesPerChannel
            };
        }
    }

    public class Mp3StreamInfo
    {
        public required int Channels { get; init; }

        public required int SampleRate { get; init; }

        public required int BitsPerSample { get; init; }

        public required long TotalSamples { get; init; }
    }
}
