using EggEncoder.Codecs.Aac;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Aac
{
    public class AacEncoderTest
    {
        [Fact]
        public void Encode_ThenDecode_Should_ReconstructToneSignal_UpToScale()
        {
            const int sampleRate = 44100;
            const int sampleCount = sampleRate;

            var originalSamples = new short[sampleCount];
            for (var i = 0; i < sampleCount; i++)
            {
                originalSamples[i] = (short)(10000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
            }

            var outputPath = Path.Combine(Path.GetTempPath(), $"aac_roundtrip_{Guid.NewGuid():N}.aac");
            try
            {
                AacEncoder.Encode(outputPath, originalSamples, channels: 1, sampleRate: sampleRate);

                var decodedSamples = new List<int>();
                var streamInfo = AacDecoder.Decode(outputPath, (block, _, _, _, _) => decodedSamples.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(1);
                streamInfo.SampleRate.Should().Be(sampleRate);

                // The encoder primes its overlap-add with a silent lookback block, so the decoder's
                // output is delayed by exactly one frame (1024 samples) relative to the original signal
                // -- the same inherent one-frame encoder delay documented for MDCT-based codecs generally.
                const int encoderDelaySamples = 1024;

                var compareLength = Math.Min(decodedSamples.Count - encoderDelaySamples, originalSamples.Length);
                compareLength.Should().BeGreaterThan(1000);

                double dotProduct = 0;
                double originalEnergy = 0;
                for (var i = 0; i < compareLength; i++)
                {
                    dotProduct += (double)decodedSamples[i + encoderDelaySamples] * originalSamples[i];
                    originalEnergy += (double)originalSamples[i] * originalSamples[i];
                }

                originalEnergy.Should().BeGreaterThan(0);

                var scale = dotProduct / originalEnergy;

                double errorEnergy = 0;
                double signalEnergy = 0;
                for (var i = 0; i < compareLength; i++)
                {
                    var expected = originalSamples[i] * scale;
                    var error = decodedSamples[i + encoderDelaySamples] - expected;
                    errorEnergy += error * error;
                    signalEnergy += expected * expected;
                }

                var signalToNoiseRatioDb = 10 * Math.Log10(signalEnergy / Math.Max(errorEnergy, 1e-9));

                signalToNoiseRatioDb.Should().BeGreaterThan(15, $"expected SNR > 15dB for own encode/decode round-trip (scale={scale}), got {signalToNoiseRatioDb}dB");
            }
            finally
            {
                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }
            }
        }

        [Fact]
        public void Encode_WithStereoChannels_Should_Throw()
        {
            var act = () => AacEncoder.Encode(Path.GetTempFileName(), new short[1024], channels: 2, sampleRate: 44100);
            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void Encode_WithUnsupportedSampleRate_Should_Throw()
        {
            var act = () => AacEncoder.Encode(Path.GetTempFileName(), new short[1024], channels: 1, sampleRate: 12345);
            act.Should().ThrowExactly<NotSupportedException>();
        }
    }
}
