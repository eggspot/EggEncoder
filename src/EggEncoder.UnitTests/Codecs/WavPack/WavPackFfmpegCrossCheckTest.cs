using EggEncoder.Codecs.Wav;
using EggEncoder.Codecs.WavPack;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.WavPack
{
    public class WavPackFfmpegCrossCheckTest
    {
        private static readonly string _wavFixturePath = Path.GetFullPath("Codecs/WavPack/sample.wav");
        private static readonly string _ffmpegWvFixturePath = Path.GetFullPath("Codecs/WavPack/sample_ffmpeg.wv");

        [Fact]
        public void Decode_FfmpegProducedWavPack_Should_Match_Original_Wav_Samples()
        {
            var expectedSamples = ReadAllSamples(_wavFixturePath, out var channels, out var sampleRate);

            var (streamInfo, decodedSamples) = WavPackTestDecoder.DecodeAll(_ffmpegWvFixturePath);

            streamInfo.Channels.Should().Be(channels);
            streamInfo.SampleRate.Should().Be(sampleRate);
            decodedSamples.Should().Equal(expectedSamples);
        }

        [Fact]
        public void Encode_SameSourceAsFfmpeg_Should_Also_Reproduce_Exact_Original_Samples()
        {
            var expectedSamples = ReadAllSamples(_wavFixturePath, out _, out _);

            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var nativeWvPath = Path.Combine(tempDirectory, "native.wv");
                WavPackEncoder.Encode(_wavFixturePath, nativeWvPath);

                var (_, decodedSamples) = WavPackTestDecoder.DecodeAll(nativeWvPath);

                decodedSamples.Should().Equal(expectedSamples);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        private static int[] ReadAllSamples(string wavFilePath, out int channels, out int sampleRate)
        {
            using var wavReader = WavReader.Open(wavFilePath);
            channels = wavReader.Channels;
            sampleRate = wavReader.SampleRate;

            var samples = new int[wavReader.TotalSamples * wavReader.Channels];
            wavReader.ReadInterleavedSamples(samples, (int)wavReader.TotalSamples);

            return samples;
        }
    }
}
