using EggEncoder.Codecs.Mp3;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Mp3
{
    // Exercises docs/managed-codec-rewrite-plan.md item 6's own measurable acceptance criteria:
    // decodability and file size across a mono/stereo x sample-rate x bitrate matrix, plus a
    // round-trip SNR floor. Unlike Opus (an explicit pre_skip field) or this project's own Vorbis
    // encoder (no such field, so VorbisEncoderSessionTest finds alignment empirically via
    // normalized cross-correlation), MP3's own hybrid filterbank + MDCT introduces a fixed,
    // content-independent encoder/decoder delay with no header field describing it either -- this
    // reuses that same empirical-alignment technique rather than assuming decoded[0] lines up with
    // original[0].
    public class Mp3EncoderSnrTest
    {
        private const double MinimumSnrDb = 10;

        [Theory]
        [InlineData(32000, 1, 64)]
        [InlineData(32000, 2, 128)]
        [InlineData(44100, 1, 128)]
        [InlineData(44100, 2, 64)]
        [InlineData(44100, 2, 320)]
        [InlineData(48000, 1, 320)]
        [InlineData(48000, 2, 128)]
        public void Encode_ToneAcrossRateChannelBitrateMatrix_Should_DecodeCleanly_And_MeetSnrFloor(int sampleRate, int channels, int bitRateKbps)
        {
            var samples = GenerateTone(sampleRate, seconds: 2, channels);

            AssertRoundTripSnr(samples, sampleRate, channels, bitRateKbps, MinimumSnrDb);
        }

        [Theory]
        [InlineData(64)]
        [InlineData(128)]
        [InlineData(320)]
        public void Encode_Tone_Should_ProduceFileSizeWithinTenPercentOfTarget(int bitRateKbps)
        {
            const int sampleRate = 44100;
            const int channels = 2;
            const int seconds = 3;

            var samples = GenerateTone(sampleRate, seconds, channels);
            var filePath = Path.Combine(Path.GetTempPath(), $"mp3_size_{Guid.NewGuid():N}.mp3");

            try
            {
                using (var session = Mp3Encoder.OpenSession(filePath, channels, sampleRate, bitsPerSample: 16, bitRateKbps))
                {
                    session.WriteInterleavedSamples(samples, samples.Length / channels);
                    session.Finish();
                }

                var actualSize = new FileInfo(filePath).Length;
                var targetSize = bitRateKbps * 1000.0 * seconds / 8.0;

                actualSize.Should().BeInRange((long)(targetSize * 0.9), (long)(targetSize * 1.1),
                    $"expected ~{targetSize} bytes for {bitRateKbps}kbps x {seconds}s, got {actualSize}");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        // Silence is exempt from the SNR check (a near-zero denominator makes dB meaningless), but
        // it must still decode cleanly -- confirming the quantizer's own "whole granule is zero"
        // degenerate path (big_values = 0, no Huffman data at all for that granule/channel) is
        // handled correctly, not just the "has real signal" path every other test here exercises.
        [Fact]
        public void Encode_Silence_Should_DecodeCleanlyWithoutThrowing()
        {
            const int sampleRate = 44100;
            const int channels = 2;
            var samples = new int[sampleRate * 2 * channels];
            var filePath = Path.Combine(Path.GetTempPath(), $"mp3_silence_{Guid.NewGuid():N}.mp3");

            try
            {
                using (var session = Mp3Encoder.OpenSession(filePath, channels, sampleRate, bitsPerSample: 16, bitRateKbps: 128))
                {
                    session.WriteInterleavedSamples(samples, samples.Length / channels);
                    session.Finish();
                }

                var act = () => Mp3TestDecoder.DecodeAll(filePath);

                act.Should().NotThrow();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        private static void AssertRoundTripSnr(int[] originalInterleaved, int sampleRate, int channels, int bitRateKbps, double minimumSnrDb)
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"mp3_snr_{Guid.NewGuid():N}.mp3");
            try
            {
                using (var session = Mp3Encoder.OpenSession(filePath, channels, sampleRate, bitsPerSample: 16, bitRateKbps))
                {
                    session.WriteInterleavedSamples(originalInterleaved, originalInterleaved.Length / channels);
                    session.Finish();
                }

                var (streamInfo, decodedSamples) = Mp3TestDecoder.DecodeAll(filePath);

                streamInfo.Channels.Should().Be(channels);
                streamInfo.SampleRate.Should().Be(sampleRate);
                decodedSamples.Should().NotBeEmpty();

                for (var channel = 0; channel < channels; channel++)
                {
                    var originalChannel = Deinterleave(originalInterleaved, channels, channel);
                    var decodedChannel = Deinterleave(decodedSamples, channels, channel);

                    var offset = FindBestAlignmentOffset(originalChannel, decodedChannel, maxOffset: sampleRate / 4);

                    var compareLength = Math.Min(decodedChannel.Count - offset, originalChannel.Count);
                    compareLength.Should().BeGreaterThan(1000);

                    double dotProduct = 0;
                    double originalEnergy = 0;
                    for (var i = 0; i < compareLength; i++)
                    {
                        dotProduct += decodedChannel[offset + i] * originalChannel[i];
                        originalEnergy += originalChannel[i] * originalChannel[i];
                    }

                    originalEnergy.Should().BeGreaterThan(0);
                    var scale = dotProduct / originalEnergy;

                    double errorEnergy = 0;
                    double signalEnergy = 0;
                    for (var i = 0; i < compareLength; i++)
                    {
                        var expected = originalChannel[i] * scale;
                        var error = decodedChannel[offset + i] - expected;
                        errorEnergy += error * error;
                        signalEnergy += expected * expected;
                    }

                    var signalToNoiseRatioDb = 10 * Math.Log10(signalEnergy / Math.Max(errorEnergy, 1e-9));

                    signalToNoiseRatioDb.Should().BeGreaterThan(minimumSnrDb, $"expected SNR > {minimumSnrDb}dB for channel {channel} (offset={offset}, scale={scale}), got {signalToNoiseRatioDb}dB");
                }
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        private static int[] GenerateTone(int sampleRate, int seconds, int channels)
        {
            var frameCount = sampleRate * seconds;
            var samples = new int[frameCount * channels];
            for (var frame = 0; frame < frameCount; frame++)
            {
                var value = (int)(8000 * Math.Sin(2 * Math.PI * 440 * frame / sampleRate));
                for (var channel = 0; channel < channels; channel++)
                {
                    samples[(frame * channels) + channel] = value;
                }
            }

            return samples;
        }

        private static List<double> Deinterleave(IReadOnlyList<int> interleaved, int channels, int channel)
        {
            var result = new List<double>();
            for (var i = channel; i < interleaved.Count; i += channels)
            {
                result.Add(interleaved[i]);
            }

            return result;
        }

        private static int FindBestAlignmentOffset(IReadOnlyList<double> original, IReadOnlyList<double> decoded, int maxOffset)
        {
            var compareLength = Math.Min(2000, original.Count);
            var bestOffset = 0;
            var bestCorrelation = double.MinValue;

            for (var offset = 0; offset <= maxOffset && offset + compareLength <= decoded.Count; offset++)
            {
                double dot = 0, originalEnergy = 0, decodedEnergy = 0;
                for (var i = 0; i < compareLength; i++)
                {
                    dot += decoded[offset + i] * original[i];
                    originalEnergy += original[i] * original[i];
                    decodedEnergy += decoded[offset + i] * decoded[offset + i];
                }

                var normalized = originalEnergy > 0 && decodedEnergy > 0 ? dot / Math.Sqrt(originalEnergy * decodedEnergy) : 0;
                if (normalized > bestCorrelation)
                {
                    bestCorrelation = normalized;
                    bestOffset = offset;
                }
            }

            return bestOffset;
        }
    }
}
