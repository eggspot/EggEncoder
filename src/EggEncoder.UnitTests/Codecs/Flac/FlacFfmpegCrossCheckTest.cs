using EggEncoder.Codecs.Flac;
using EggEncoder.Codecs.Wav;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Flac
{
    public class FlacFfmpegCrossCheckTest
    {
        private static readonly string _wavFixturePath = Path.GetFullPath("Codecs/Flac/sample.wav");
        private static readonly string _ffmpegFlacFixturePath = Path.GetFullPath("Codecs/Flac/sample_ffmpeg.flac");
        private static readonly string _wav24BitMonoFixturePath = Path.GetFullPath("Codecs/Flac/sample_24bit_mono.wav");
        private static readonly string _ffmpegFlac24BitMonoFixturePath = Path.GetFullPath("Codecs/Flac/sample_24bit_mono_ffmpeg.flac");
        private static readonly string _wav24BitStereoFixturePath = Path.GetFullPath("Codecs/Flac/sample_24bit_stereo.wav");
        private static readonly string _ffmpegFlac24BitStereoFixturePath = Path.GetFullPath("Codecs/Flac/sample_24bit_stereo_ffmpeg.flac");

        [Fact]
        public void Decode_FfmpegProducedFlac_Should_Match_Original_Wav_Samples()
        {
            var expectedSamples = ReadAllSamples(_wavFixturePath, out var channels, out var sampleRate);

            var (streamInfo, decodedSamples) = FlacTestDecoder.DecodeAll(_ffmpegFlacFixturePath);

            streamInfo.Channels.Should().Be(channels);
            streamInfo.SampleRate.Should().Be(sampleRate);
            decodedSamples.Should().Equal(expectedSamples);
        }

        [Fact]
        public void Decode_FfmpegProduced24BitMonoFlac_Should_Match_Original_Wav_Samples()
        {
            // Covers a bit depth (24) and channel count (mono) this file's own sample_ffmpeg.flac
            // (16-bit stereo) doesn't exercise, generated fresh from real ffmpeg on the machine this
            // was authored on, the same way sample_ffmpeg.flac itself was.
            var expectedSamples = ReadAllSamples(_wav24BitMonoFixturePath, out var channels, out var sampleRate);

            var (streamInfo, decodedSamples) = FlacTestDecoder.DecodeAll(_ffmpegFlac24BitMonoFixturePath);

            streamInfo.Channels.Should().Be(channels);
            streamInfo.SampleRate.Should().Be(sampleRate);
            streamInfo.BitsPerSample.Should().Be(24);
            decodedSamples.Should().Equal(expectedSamples);
        }

        [Fact]
        public void Decode_FfmpegProduced24BitStereoFlac_Should_Match_Original_Wav_Samples()
        {
            // Distinct per-channel tones (not the same tone duplicated) so real stereo decorrelation
            // (left/side, right/side, or mid/side -- whichever ffmpeg's own encoder picked) is
            // genuinely exercised, not trivially satisfied by two identical channels.
            var expectedSamples = ReadAllSamples(_wav24BitStereoFixturePath, out var channels, out var sampleRate);

            var (streamInfo, decodedSamples) = FlacTestDecoder.DecodeAll(_ffmpegFlac24BitStereoFixturePath);

            streamInfo.Channels.Should().Be(channels);
            streamInfo.SampleRate.Should().Be(sampleRate);
            streamInfo.BitsPerSample.Should().Be(24);
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
                var nativeFlacPath = Path.Combine(tempDirectory, "native.flac");
                FlacEncoder.Encode(_wavFixturePath, nativeFlacPath);

                var (_, decodedSamples) = FlacTestDecoder.DecodeAll(nativeFlacPath);

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
