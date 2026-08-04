using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Mp3
{
    public class Mp3DecoderTest
    {
        [Fact]
        public void Decode_ToneMp3_Should_Produce_Correct_Format_And_NonSilent_Samples()
        {
            var (streamInfo, samples) = Mp3TestDecoder.DecodeAll(Path.GetFullPath("Codecs/Mp3/tone.mp3"));

            streamInfo.Channels.Should().Be(2);
            streamInfo.SampleRate.Should().Be(44100);
            streamInfo.BitsPerSample.Should().Be(16);
            samples.Should().NotBeEmpty();

            var rootMeanSquare = Math.Sqrt(samples.Average(sample => (double)sample * sample));
            rootMeanSquare.Should().BeGreaterThan(1000, $"expected a real, non-silent decoded signal, got RMS={rootMeanSquare}");
        }
    }
}
