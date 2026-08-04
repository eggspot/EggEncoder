using EggEncoder.Codecs.Aac;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Aac
{
    public class AacDecoderTest
    {
        private static readonly string _fixturePath = Path.GetFullPath("Codecs/Aac/tone_mono.aac");
        private static readonly string _groundTruthPath = Path.GetFullPath("Codecs/Aac/tone_mono_groundtruth.pcm");

        [Fact]
        public void Decode_ToneMono_Should_Return_Correct_Format()
        {
            var samples = new List<int>();
            var streamInfo = AacDecoder.Decode(_fixturePath, (block, _, _, _, _) => samples.AddRange(block.ToArray()));

            streamInfo.Channels.Should().Be(1);
            streamInfo.SampleRate.Should().Be(44100);
            samples.Should().NotBeEmpty();
        }

        [Fact]
        public void Decode_ToneMono_Should_MatchFfmpegGroundTruth_UpToScale()
        {
            var decodedSamples = new List<int>();
            AacDecoder.Decode(_fixturePath, (block, _, _, _, _) => decodedSamples.AddRange(block.ToArray()));

            var groundTruthBytes = File.ReadAllBytes(_groundTruthPath);
            var groundTruthSamples = new int[groundTruthBytes.Length / 2];
            for (var i = 0; i < groundTruthSamples.Length; i++)
            {
                groundTruthSamples[i] = (short)(groundTruthBytes[i * 2] | (groundTruthBytes[(i * 2) + 1] << 8));
            }

            var compareLength = Math.Min(decodedSamples.Count, groundTruthSamples.Length);
            compareLength.Should().BeGreaterThan(1000, "expected a meaningful number of comparable samples");

            double dotProduct = 0;
            double groundTruthEnergy = 0;
            for (var i = 0; i < compareLength; i++)
            {
                dotProduct += (double)decodedSamples[i] * groundTruthSamples[i];
                groundTruthEnergy += (double)groundTruthSamples[i] * groundTruthSamples[i];
            }

            groundTruthEnergy.Should().BeGreaterThan(0, "ground truth signal must not be silent");

            var scale = dotProduct / groundTruthEnergy;

            double errorEnergy = 0;
            double signalEnergy = 0;
            for (var i = 0; i < compareLength; i++)
            {
                var expected = groundTruthSamples[i] * scale;
                var error = decodedSamples[i] - expected;
                errorEnergy += error * error;
                signalEnergy += expected * expected;
            }

            var signalToNoiseRatioDb = 10 * Math.Log10(signalEnergy / Math.Max(errorEnergy, 1e-9));

            signalToNoiseRatioDb.Should().BeGreaterThan(20, $"expected SNR > 20dB against ground truth (scale={scale}), got {signalToNoiseRatioDb}dB");
        }

        [Fact]
        public void Decode_WithMissingFile_Should_Throw()
        {
            var act = () => AacDecoder.Decode(Path.GetFullPath("Codecs/Aac/does_not_exist.aac"), (_, _, _, _, _) => { });
            act.Should().ThrowExactly<FileNotFoundException>();
        }
    }
}
