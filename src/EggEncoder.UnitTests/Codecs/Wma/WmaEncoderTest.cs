using EggEncoder.Codecs.Wma;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Wma
{
    public class WmaEncoderTest
    {
        [Fact]
        public void Encode_ThenDecode_Mono_Should_ReconstructToneSignal_UpToScale()
        {
            const int sampleRate = 44100;
            const int channels = 1;

            var originalSamples = GenerateInterleavedTone(sampleRate, seconds: 1, channels);
            var outputPath = Path.Combine(Path.GetTempPath(), $"wma_roundtrip_mono_{Guid.NewGuid():N}.wma");

            try
            {
                using (var session = WmaEncoderSession.OpenSession(outputPath, channels, sampleRate))
                {
                    session.WriteInterleavedSamples(originalSamples, originalSamples.Length / channels);
                    session.Finish();
                }

                AssertRoundTripSnr(outputPath, originalSamples, channels, sampleRate, minimumSnrDb: 10);
            }
            finally
            {
                File.Delete(outputPath);
            }
        }

        [Fact]
        public void Encode_ThenDecode_Stereo_Should_ReconstructToneSignal_UpToScale()
        {
            const int sampleRate = 44100;
            const int channels = 2;

            var originalSamples = GenerateInterleavedTone(sampleRate, seconds: 1, channels);
            var outputPath = Path.Combine(Path.GetTempPath(), $"wma_roundtrip_stereo_{Guid.NewGuid():N}.wma");

            try
            {
                using (var session = WmaEncoderSession.OpenSession(outputPath, channels, sampleRate))
                {
                    session.WriteInterleavedSamples(originalSamples, originalSamples.Length / channels);
                    session.Finish();
                }

                AssertRoundTripSnr(outputPath, originalSamples, channels, sampleRate, minimumSnrDb: 10);
            }
            finally
            {
                File.Delete(outputPath);
            }
        }

        [Fact]
        public void Encode_WholeBufferApi_Should_ProduceDecodableFile()
        {
            const int sampleRate = 44100;
            const int channels = 1;

            var samples = new short[sampleRate];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (short)(10000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
            }

            var outputPath = Path.Combine(Path.GetTempPath(), $"wma_wholebuffer_{Guid.NewGuid():N}.wma");

            try
            {
                WmaEncoder.Encode(outputPath, samples, channels, sampleRate);

                var streamInfo = WmaDecoder.Decode(outputPath, (_, _, _, _, _) => { });
                streamInfo.Channels.Should().Be(1);
                streamInfo.SampleRate.Should().Be(sampleRate);
                streamInfo.TotalSamples.Should().BeGreaterThan(0);
            }
            finally
            {
                File.Delete(outputPath);
            }
        }

        [Fact]
        public void OpenSession_WithUnsupportedChannelCount_Should_Throw()
        {
            var outputPath = Path.Combine(Path.GetTempPath(), $"wma_invalid_{Guid.NewGuid():N}.wma");

            var act = () => WmaEncoderSession.OpenSession(outputPath, channels: 3, sampleRate: 44100);

            act.Should().ThrowExactly<NotSupportedException>();
            File.Exists(outputPath).Should().BeFalse();
        }

        private static int[] GenerateInterleavedTone(int sampleRate, int seconds, int channels)
        {
            var sampleCount = sampleRate * seconds;
            var samples = new int[sampleCount * channels];

            for (var i = 0; i < sampleCount; i++)
            {
                var value = (short)(10000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
                for (var channel = 0; channel < channels; channel++)
                {
                    samples[(i * channels) + channel] = value;
                }
            }

            return samples;
        }

        private static void AssertRoundTripSnr(string wmaPath, int[] originalInterleaved, int channels, int sampleRate, double minimumSnrDb)
        {
            var decodedSamples = new List<int>();
            var streamInfo = WmaDecoder.Decode(wmaPath, (block, _, _, _, _) => decodedSamples.AddRange(block.ToArray()));

            streamInfo.Channels.Should().Be(channels);
            streamInfo.SampleRate.Should().Be(sampleRate);
            decodedSamples.Should().NotBeEmpty();

            // The encoder primes its overlap-add with a silent lookback block, so the decoder's
            // output is delayed by exactly one frame relative to the original signal -- the same
            // inherent one-frame encoder delay documented for AacEncoderTest.
            var encoderDelaySamples = 1 << WmaTables.GetFrameLengthBits(sampleRate);

            for (var channel = 0; channel < channels; channel++)
            {
                var originalChannel = new List<double>();
                for (var i = channel; i < originalInterleaved.Length; i += channels)
                {
                    originalChannel.Add(originalInterleaved[i]);
                }

                var decodedChannel = new List<double>();
                for (var i = channel; i < decodedSamples.Count; i += channels)
                {
                    decodedChannel.Add(decodedSamples[i]);
                }

                var compareLength = Math.Min(decodedChannel.Count - encoderDelaySamples, originalChannel.Count);
                compareLength.Should().BeGreaterThan(1000);

                double dotProduct = 0;
                double originalEnergy = 0;
                for (var i = 0; i < compareLength; i++)
                {
                    dotProduct += decodedChannel[i + encoderDelaySamples] * originalChannel[i];
                    originalEnergy += originalChannel[i] * originalChannel[i];
                }

                originalEnergy.Should().BeGreaterThan(0);
                var scale = dotProduct / originalEnergy;

                double errorEnergy = 0;
                double signalEnergy = 0;
                for (var i = 0; i < compareLength; i++)
                {
                    var expected = originalChannel[i] * scale;
                    var error = decodedChannel[i + encoderDelaySamples] - expected;
                    errorEnergy += error * error;
                    signalEnergy += expected * expected;
                }

                var signalToNoiseRatioDb = 10 * Math.Log10(signalEnergy / Math.Max(errorEnergy, 1e-9));

                signalToNoiseRatioDb.Should().BeGreaterThan(minimumSnrDb, $"expected SNR > {minimumSnrDb}dB for channel {channel} (scale={scale}), got {signalToNoiseRatioDb}dB");
            }
        }
    }
}
