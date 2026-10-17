using EggEncoder.Streaming;
using EggEncoder.Streaming.Mp3;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Streaming.Mp3
{
    public class Mp3StreamingEncoderTest
    {
        private const int SampleRate = 44100;

        [Fact]
        public void Create_WithNullCallback_Should_Throw()
        {
            var act = () => Mp3StreamingEncoder.Create(1, SampleRate, null!);

            act.Should().ThrowExactly<ArgumentNullException>();
        }

        [Fact]
        public void Create_WithUnsupportedChannelCount_Should_Throw()
        {
            var act = () => Mp3StreamingEncoder.Create(3, SampleRate, _ => { });

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void Create_WithUnsupportedSampleRate_Should_Throw()
        {
            var act = () => Mp3StreamingEncoder.Create(1, 22050, _ => { });

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void Create_WithUnsupportedBitRate_Should_Throw()
        {
            var act = () => Mp3StreamingEncoder.Create(1, SampleRate, _ => { }, bitRateKbps: 123);

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void Create_WithValidParameters_Should_ExposeChannelsSampleRateAndFrameSize()
        {
            using var encoder = Mp3StreamingEncoder.Create(2, SampleRate, _ => { });

            encoder.Channels.Should().Be(2);
            encoder.SampleRate.Should().Be(SampleRate);
            encoder.SamplesPerFrame.Should().Be(1152);
        }

        [Fact]
        public void Complete_WithNoSamplesWritten_Should_EmitNoFrames()
        {
            var emitted = false;
            using var encoder = Mp3StreamingEncoder.Create(1, SampleRate, _ => emitted = true);

            encoder.Complete();

            emitted.Should().BeFalse();
        }

        [Fact]
        public void Complete_CalledTwice_Should_BeIdempotent()
        {
            var emitCount = 0;
            using var encoder = Mp3StreamingEncoder.Create(1, SampleRate, _ => emitCount++);
            encoder.Write(GenerateTone(seconds: 1, channels: 1));

            encoder.Complete();
            var countAfterFirstComplete = emitCount;
            encoder.Complete();

            emitCount.Should().Be(countAfterFirstComplete);
        }

        [Fact]
        public void Write_AfterComplete_Should_Throw()
        {
            using var encoder = Mp3StreamingEncoder.Create(1, SampleRate, _ => { });
            encoder.Complete();

            var act = () => encoder.Write(new[] { 1, 2, 3 });

            act.Should().ThrowExactly<InvalidOperationException>();
        }

        [Fact]
        public void Dispose_CalledTwice_Should_BeSafe()
        {
            var encoder = Mp3StreamingEncoder.Create(1, SampleRate, _ => { });

            encoder.Dispose();
            var act = encoder.Dispose;

            act.Should().NotThrow();
        }

        [Fact]
        public void Dispose_WithoutCallingComplete_Should_BeSafe()
        {
            var encoder = Mp3StreamingEncoder.Create(1, SampleRate, _ => { });
            encoder.Write(new[] { 1, 2, 3 });

            var act = encoder.Dispose;

            act.Should().NotThrow();
        }

        [Fact]
        public void FrameIndex_Should_IncrementMonotonically()
        {
            var frameIndices = new List<long>();
            using var encoder = Mp3StreamingEncoder.Create(1, SampleRate, frame => frameIndices.Add(frame.FrameIndex));

            encoder.Write(GenerateTone(seconds: 1, channels: 1));
            encoder.Complete();

            frameIndices.Should().Equal(Enumerable.Range(0, frameIndices.Count).Select(i => (long)i));
        }

        [Fact]
        public void Complete_WithPartialFrame_Should_ReportRealCountForTailFrame_AndZeroForFlushFrame()
        {
            var sampleCounts = new List<int>();
            using var encoder = Mp3StreamingEncoder.Create(1, SampleRate, frame => sampleCounts.Add(frame.SampleCount));

            encoder.Write(GenerateToneFrames(frameCount: 300, channels: 1)); // less than one 1152-sample frame
            sampleCounts.Should().BeEmpty();

            encoder.Complete();

            sampleCounts.Should().Equal(300, 0);
        }

        [Fact]
        public void Complete_WithExactlyOneFullFrame_Should_StillEmitAZeroSampleFlushFrame()
        {
            var sampleCounts = new List<int>();
            using var encoder = Mp3StreamingEncoder.Create(1, SampleRate, frame => sampleCounts.Add(frame.SampleCount));

            encoder.Write(GenerateToneFrames(frameCount: 1152, channels: 1)); // exactly one full frame, nothing pending
            sampleCounts.Should().Equal(1152);

            encoder.Complete();

            sampleCounts.Should().Equal(new[] { 1152, 0 }, "the mandatory flush frame is still required to drain the encoder's own one-granule MDCT history, even with nothing partial pending");
        }

        [Fact]
        public void Mono_Should_RoundTrip_WithGoodSnr()
        {
            AssertRoundTripSnr(GenerateTone(seconds: 2, channels: 1), channels: 1, minimumSnrDb: 10);
        }

        [Fact]
        public void Stereo_Should_RoundTrip_WithGoodSnr()
        {
            AssertRoundTripSnr(GenerateTone(seconds: 2, channels: 2), channels: 2, minimumSnrDb: 10);
        }

        // Mirrors Mp3EncoderSnrTest's own "Encode_Should_PreserveTheExactTailOfRealAudio" regression
        // test: the mandatory flush frame this encoder's Complete() emits is what makes the true
        // tail of real audio recoverable at all (see its own doc comment) -- confirms this streaming
        // encoder's restructured version of that same fix still has the property, not just that it
        // compiles and emits the right frame count.
        [Fact]
        public void Complete_Should_PreserveTheExactTailOfRealAudio_NotJustAnApproximateDuration()
        {
            const int channels = 1;
            var rng = new Random(13579);
            var samples = new int[SampleRate * 2];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (int)((rng.NextDouble() * 2.0 - 1.0) * 20000);
            }

            var frameBytes = new List<byte[]>();
            using (var encoder = Mp3StreamingEncoder.Create(channels, SampleRate, frame => frameBytes.Add(frame.Payload.ToArray())))
            {
                encoder.Write(samples);
                encoder.Complete();
            }

            var filePath = Path.Combine(Path.GetTempPath(), $"mp3_streaming_tail_{Guid.NewGuid():N}.mp3");
            try
            {
                File.WriteAllBytes(filePath, frameBytes.SelectMany(f => f).ToArray());

                var (_, decodedSamples) = Mp3TestDecoder.DecodeAll(filePath);

                var original = ToDoubleList(samples);
                var decoded = ToDoubleList(decodedSamples);
                var offset = FindBestAlignmentOffset(original, decoded, maxOffset: SampleRate / 2);

                var compareLength = decoded.Count - offset;
                compareLength.Should().BeGreaterThanOrEqualTo(original.Count, "the full tail of real audio must be recoverable from the bitstream, not just most of it");

                const int tailWindow = 200;
                var tailStart = original.Count - tailWindow;
                double dot = 0, originalEnergy = 0, decodedEnergy = 0;
                for (var i = tailStart; i < original.Count; i++)
                {
                    dot += decoded[offset + i] * original[i];
                    originalEnergy += original[i] * original[i];
                    decodedEnergy += decoded[offset + i] * decoded[offset + i];
                }

                var correlation = dot / Math.Sqrt(originalEnergy * decodedEnergy);
                correlation.Should().BeGreaterThan(0.99, $"expected the last {tailWindow} real samples to correlate almost perfectly with the original, got {correlation}");
            }
            finally
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
        }

        private static void AssertRoundTripSnr(int[] originalInterleaved, int channels, double minimumSnrDb)
        {
            var frameBytes = new List<byte[]>();
            using (var encoder = Mp3StreamingEncoder.Create(channels, SampleRate, frame => frameBytes.Add(frame.Payload.ToArray())))
            {
                encoder.Write(originalInterleaved);
                encoder.Complete();
            }

            var filePath = Path.Combine(Path.GetTempPath(), $"mp3_streaming_snr_{Guid.NewGuid():N}.mp3");
            try
            {
                File.WriteAllBytes(filePath, frameBytes.SelectMany(f => f).ToArray());

                var (streamInfo, decodedSamples) = Mp3TestDecoder.DecodeAll(filePath);

                streamInfo.Channels.Should().Be(channels);
                streamInfo.SampleRate.Should().Be(SampleRate);
                decodedSamples.Should().NotBeEmpty();

                for (var channel = 0; channel < channels; channel++)
                {
                    var originalChannel = Deinterleave(originalInterleaved, channels, channel);
                    var decodedChannel = Deinterleave(decodedSamples, channels, channel);

                    var offset = FindBestAlignmentOffset(originalChannel, decodedChannel, maxOffset: SampleRate / 4);

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
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
        }

        private static int[] GenerateTone(int seconds, int channels) => GenerateToneFrames(SampleRate * seconds, channels);

        private static int[] GenerateToneFrames(int frameCount, int channels)
        {
            var samples = new int[frameCount * channels];
            for (var frame = 0; frame < frameCount; frame++)
            {
                var value = (int)(8000 * Math.Sin(2 * Math.PI * 440 * frame / SampleRate));
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

        private static List<double> ToDoubleList(IReadOnlyList<int> values) => values.Select(v => (double)v).ToList();

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
