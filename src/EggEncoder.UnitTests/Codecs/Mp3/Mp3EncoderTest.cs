using EggEncoder.Codecs.Mp3;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Mp3
{
    public class Mp3EncoderTest
    {
        private static readonly string _wavFixturePath = Path.GetFullPath("Codecs/Flac/sample.wav");

        [Fact]
        public void Encode_Should_Produce_Valid_Decodable_Mp3_With_Correct_Format()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var mp3Path = Path.Combine(tempDirectory, "output.mp3");
                Mp3Encoder.Encode(_wavFixturePath, mp3Path);

                var probeResult = Mp3Probe.Probe(mp3Path);
                probeResult.SampleRate.Should().Be(44100);
                probeResult.Channels.Should().Be(2);
                probeResult.DurationInSeconds.Should().Be(2);

                var (streamInfo, samples) = Mp3TestDecoder.DecodeAll(mp3Path);
                streamInfo.Channels.Should().Be(2);
                samples.Should().NotBeEmpty();

                var rootMeanSquare = Math.Sqrt(samples.Average(sample => (double)sample * sample));
                rootMeanSquare.Should().BeGreaterThan(1000, $"expected a real, non-silent decoded signal, got RMS={rootMeanSquare}");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Encode_WithLowerBitRate_Should_Produce_Smaller_File()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var highBitRatePath = Path.Combine(tempDirectory, "high.mp3");
                var lowBitRatePath = Path.Combine(tempDirectory, "low.mp3");

                Mp3Encoder.Encode(_wavFixturePath, highBitRatePath, bitRateKbps: 320);
                Mp3Encoder.Encode(_wavFixturePath, lowBitRatePath, bitRateKbps: 64);

                var highBitRateSize = new FileInfo(highBitRatePath).Length;
                var lowBitRateSize = new FileInfo(lowBitRatePath).Length;

                lowBitRateSize.Should().BeLessThan(highBitRateSize, $"expected 64kbps ({lowBitRateSize} bytes) to be smaller than 320kbps ({highBitRateSize} bytes)");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }
}
