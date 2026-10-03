using EggEncoder.Codecs.Opus;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Opus
{
    public class OpusEncoderSessionTest
    {
        private const int SampleRate = 48000;

        [Fact]
        public void OpenSession_With_UnsupportedChannelCount_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var act = () => OpusEncoderSession.OpenSession(filePath, channels: 3, SampleRate, bitsPerSample: 16);

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void OpenSession_With_UnsupportedBitDepth_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var act = () => OpusEncoderSession.OpenSession(filePath, channels: 1, SampleRate, bitsPerSample: 24);

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void OpenSession_With_UnsupportedSampleRate_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var act = () => OpusEncoderSession.OpenSession(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16);

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_Then_Finish_Should_RoundTrip_Mono_WithGoodSnr()
        {
            var samples = GenerateTone(seconds: 2, channels: 1);

            AssertRoundTripSnr(samples, channels: 1, minimumSnrDb: 15);
        }

        [Fact]
        public void WriteInterleavedSamples_Then_Finish_Should_RoundTrip_Stereo_WithGoodSnr()
        {
            var samples = GenerateTone(seconds: 2, channels: 2);

            AssertRoundTripSnr(samples, channels: 2, minimumSnrDb: 15);
        }

        [Fact]
        public void WriteInterleavedSamples_CalledInSmallChunks_Should_StillProduceAValidFile()
        {
            var samples = GenerateTone(seconds: 1, channels: 1);
            var filePath = Path.Combine(Path.GetTempPath(), $"opus_small_chunks_{Guid.NewGuid():N}.opus");

            try
            {
                using (var session = OpusEncoderSession.OpenSession(filePath, channels: 1, SampleRate, bitsPerSample: 16))
                {
                    const int chunkSize = 37; // deliberately not a divisor of the 960-sample Opus frame
                    for (var offset = 0; offset < samples.Length; offset += chunkSize)
                    {
                        var count = Math.Min(chunkSize, samples.Length - offset);
                        session.WriteInterleavedSamples(samples[offset..(offset + count)], count);
                    }

                    session.Finish();
                }

                var decoded = new List<int>();
                var streamInfo = OpusDecoder.Decode(filePath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(1);
                decoded.Should().NotBeEmpty();
                decoded.Count.Should().Be((int)streamInfo.TotalSamples);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Finish_WithNoSamplesWritten_Should_Produce_An_EmptyButValidFile()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"opus_empty_{Guid.NewGuid():N}.opus");
            try
            {
                using (var session = OpusEncoderSession.OpenSession(filePath, channels: 1, SampleRate, bitsPerSample: 16))
                {
                    session.Finish();
                }

                var streamInfo = OpusDecoder.Decode(filePath, (_, _, _, _, _) => { });
                streamInfo.TotalSamples.Should().Be(0);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Dispose_CalledTwice_Should_Not_Throw()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"opus_dispose_{Guid.NewGuid():N}.opus");
            try
            {
                var session = OpusEncoderSession.OpenSession(filePath, channels: 1, SampleRate, bitsPerSample: 16);
                session.Dispose();

                var act = session.Dispose;
                act.Should().NotThrow();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_SingleCallSpanningMultipleFrames_Should_RoundTrip()
        {
            var samples = GenerateTone(seconds: 3, channels: 1); // several 960-sample frames in one call

            AssertRoundTripSnr(samples, channels: 1, minimumSnrDb: 15);
        }

        private static int[] GenerateTone(int seconds, int channels)
        {
            var frameCount = SampleRate * seconds;
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

        private static void AssertRoundTripSnr(int[] originalInterleaved, int channels, double minimumSnrDb)
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"opus_session_{Guid.NewGuid():N}.opus");
            try
            {
                using (var session = OpusEncoderSession.OpenSession(filePath, channels, SampleRate, bitsPerSample: 16))
                {
                    session.WriteInterleavedSamples(originalInterleaved, originalInterleaved.Length / channels);
                    session.Finish();
                }

                var decodedSamples = new List<int>();
                var streamInfo = OpusDecoder.Decode(filePath, (block, decodedChannels, rate, bits, total) =>
                {
                    decodedChannels.Should().Be(channels);
                    rate.Should().Be(SampleRate);
                    bits.Should().Be(16);
                    decodedSamples.AddRange(block.ToArray());
                });

                streamInfo.Channels.Should().Be(channels);
                streamInfo.SampleRate.Should().Be(SampleRate);
                decodedSamples.Should().NotBeEmpty();

                // Opus's fixed frame size means the encoded stream is always a whole multiple of
                // 960 samples per channel, so it's never shorter than the original and may run up
                // to one frame longer (trailing silence from draining the encoder's lookahead and
                // padding the final partial frame) -- unlike WmaEncoderTest's codec, this
                // implementation's own decoder already strips the encoder's lookahead delay via
                // OpusHead's pre_skip, so (unlike that test) no further manual offset is needed
                // here: decoded[0] already lines up with original[0].
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

                    var compareLength = Math.Min(decodedChannel.Count, originalChannel.Count);
                    compareLength.Should().BeGreaterThan(1000);

                    double dotProduct = 0;
                    double originalEnergy = 0;
                    for (var i = 0; i < compareLength; i++)
                    {
                        dotProduct += decodedChannel[i] * originalChannel[i];
                        originalEnergy += originalChannel[i] * originalChannel[i];
                    }

                    originalEnergy.Should().BeGreaterThan(0);
                    var scale = dotProduct / originalEnergy;

                    double errorEnergy = 0;
                    double signalEnergy = 0;
                    for (var i = 0; i < compareLength; i++)
                    {
                        var expected = originalChannel[i] * scale;
                        var error = decodedChannel[i] - expected;
                        errorEnergy += error * error;
                        signalEnergy += expected * expected;
                    }

                    var signalToNoiseRatioDb = 10 * Math.Log10(signalEnergy / Math.Max(errorEnergy, 1e-9));

                    signalToNoiseRatioDb.Should().BeGreaterThan(minimumSnrDb, $"expected SNR > {minimumSnrDb}dB for channel {channel} (scale={scale}), got {signalToNoiseRatioDb}dB");
                }
            }
            finally
            {
                File.Delete(filePath);
            }
        }
    }
}
