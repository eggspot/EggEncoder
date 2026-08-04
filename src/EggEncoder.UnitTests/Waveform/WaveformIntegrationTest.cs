using EggEncoder.Codecs.Flac;
using EggEncoder.Codecs.Wav;
using EggEncoder.Waveform;
using FluentAssertions;

namespace EggEncoder.UnitTests.Waveform
{
    public class WaveformIntegrationTest
    {
        private static readonly string _wavFixturePath = Path.GetFullPath("Codecs/Flac/sample.wav");
        private static readonly string _ffmpegFlacFixturePath = Path.GetFullPath("Codecs/Flac/sample_ffmpeg.flac");

        [Fact]
        public void Calculate_From_WavReader_Should_Produce_Bounded_NonZero_Windows()
        {
            using var wavReader = WavReader.Open(_wavFixturePath);
            var calculator = new WaveformCalculator(wavReader.TotalSamples, wavReader.Channels, wavReader.BitsPerSample);

            var buffer = new int[4096 * wavReader.Channels];
            int framesRead;
            while ((framesRead = wavReader.ReadInterleavedSamples(buffer, 4096)) > 0)
            {
                calculator.AddBlock(new ReadOnlySpan<int>(buffer, 0, framesRead * wavReader.Channels));
            }

            var windows = calculator.GetNormalizedWindows();

            windows.Should().NotBeEmpty();
            windows.TrueForAll(window => window is >= 0 and <= 1).Should().BeTrue();
            windows.Exists(window => window > 0).Should().BeTrue();
        }

        [Fact]
        public void Calculate_From_FlacDecoder_Should_Produce_Bounded_NonZero_Windows()
        {
            WaveformCalculator? calculator = null;

            FlacDecoder.Decode(_ffmpegFlacFixturePath, (block, channels, _, bitsPerSample, totalSamplesPerChannel) =>
            {
                calculator ??= new WaveformCalculator(totalSamplesPerChannel, channels, bitsPerSample);
                calculator.AddBlock(block);
            });

            calculator.Should().NotBeNull();
            var windows = calculator!.GetNormalizedWindows();

            windows.Should().NotBeEmpty();
            windows.TrueForAll(window => window is >= 0 and <= 1).Should().BeTrue();
            windows.Exists(window => window > 0).Should().BeTrue();
        }
    }
}
