using EggEncoder.Codecs.Opus;
using EggEncoder.Streaming;
using EggEncoder.Streaming.Opus;
using FluentAssertions;

namespace EggEncoder.UnitTests.Streaming.Opus
{
    public class OpusStreamingEncoderTest
    {
        private const int SampleRate = 48000;

        [Fact]
        public void Create_WithNullCallback_Should_Throw()
        {
            var act = () => OpusStreamingEncoder.Create(1, SampleRate, null!);

            act.Should().ThrowExactly<ArgumentNullException>();
        }

        [Fact]
        public void Create_WithUnsupportedChannelCount_Should_Throw()
        {
            var act = () => OpusStreamingEncoder.Create(3, SampleRate, _ => { });

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void Create_WithUnsupportedSampleRate_Should_Throw()
        {
            var act = () => OpusStreamingEncoder.Create(1, 44100, _ => { });

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void Create_WithValidParameters_Should_ExposeChannelsSampleRateAndFrameSize()
        {
            using var encoder = OpusStreamingEncoder.Create(2, SampleRate, _ => { });

            encoder.Channels.Should().Be(2);
            encoder.SampleRate.Should().Be(SampleRate);
            encoder.SamplesPerFrame.Should().Be(960);
        }

        [Fact]
        public void EveryFrame_IncludingTheFinalOne_Should_ReportFullFrameSampleCount()
        {
            var sampleCounts = new List<int>();
            using var encoder = OpusStreamingEncoder.Create(1, SampleRate, frame => sampleCounts.Add(frame.SampleCount));

            encoder.Write(GenerateTone(seconds: 1, channels: 1));
            encoder.Complete();

            sampleCounts.Should().NotBeEmpty();
            sampleCounts.Should().OnlyContain(count => count == 960, "Opus has no bitstream-level short-final-frame concept, unlike AacStreamingEncoder");
        }

        [Fact]
        public void FrameIndex_Should_IncrementMonotonically()
        {
            var frameIndices = new List<long>();
            using var encoder = OpusStreamingEncoder.Create(1, SampleRate, frame => frameIndices.Add(frame.FrameIndex));

            encoder.Write(GenerateTone(seconds: 1, channels: 1));
            encoder.Complete();

            frameIndices.Should().BeInAscendingOrder();
            frameIndices.Should().Equal(Enumerable.Range(0, frameIndices.Count).Select(i => (long)i));
        }

        [Fact]
        public void Complete_WithNoSamplesWritten_Should_EmitNoFrames()
        {
            var emitted = false;
            using var encoder = OpusStreamingEncoder.Create(1, SampleRate, _ => emitted = true);

            encoder.Complete();

            emitted.Should().BeFalse();
        }

        [Fact]
        public void Complete_CalledTwice_Should_BeIdempotent()
        {
            var emitCount = 0;
            using var encoder = OpusStreamingEncoder.Create(1, SampleRate, _ => emitCount++);
            encoder.Write(GenerateTone(seconds: 1, channels: 1));

            encoder.Complete();
            var countAfterFirstComplete = emitCount;
            encoder.Complete();

            emitCount.Should().Be(countAfterFirstComplete);
        }

        [Fact]
        public void Write_AfterComplete_Should_Throw()
        {
            using var encoder = OpusStreamingEncoder.Create(1, SampleRate, _ => { });
            encoder.Complete();

            var act = () => encoder.Write(new[] { 1, 2, 3 });

            act.Should().ThrowExactly<InvalidOperationException>();
        }

        [Fact]
        public void Dispose_CalledTwice_Should_BeSafe()
        {
            var encoder = OpusStreamingEncoder.Create(1, SampleRate, _ => { });

            encoder.Dispose();
            var act = encoder.Dispose;

            act.Should().NotThrow();
        }

        [Fact]
        public void Dispose_WithoutCallingComplete_Should_BeSafe()
        {
            var encoder = OpusStreamingEncoder.Create(1, SampleRate, _ => { });
            encoder.Write(new[] { 1, 2, 3 });

            var act = encoder.Dispose;

            act.Should().NotThrow();
        }

        [Fact]
        public void Mono_Should_RoundTrip_WithGoodSnr()
        {
            AssertRoundTripSnr(GenerateTone(seconds: 2, channels: 1), channels: 1, minimumSnrDb: 15);
        }

        [Fact]
        public void Stereo_Should_RoundTrip_WithGoodSnr()
        {
            AssertRoundTripSnr(GenerateTone(seconds: 2, channels: 2), channels: 2, minimumSnrDb: 15);
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

        // Reconstructs a full, valid OggOpus file from this encoder's own raw packets (the same
        // header-packet/page-writing machinery OpusEncoderSession itself uses internally), then
        // decodes it through the project's own public OpusDecoder -- proving the streaming
        // encoder's raw packets are genuinely valid, decodable Opus, without needing any new
        // decode-side test infrastructure.
        private static void AssertRoundTripSnr(int[] originalInterleaved, int channels, double minimumSnrDb)
        {
            var packets = new List<byte[]>();
            int lookahead;

            using (var encoder = (OpusStreamingEncoder)OpusStreamingEncoder.Create(channels, SampleRate, frame => packets.Add(frame.Payload.ToArray())))
            {
                encoder.Write(originalInterleaved);
                encoder.Complete();
                lookahead = encoder.EncoderDelaySamples;
            }

            var filePath = Path.Combine(Path.GetTempPath(), $"opus_streaming_{Guid.NewGuid():N}.opus");
            try
            {
                using (var destStream = File.Create(filePath))
                {
                    var pageWriter = new OggPageWriter(destStream, serialNumber: 1);
                    pageWriter.WritePacket(OpusHeaderPackets.BuildOpusHead(channels, lookahead, SampleRate), granulePosition: 0, isEndOfStream: false);
                    pageWriter.WritePacket(OpusHeaderPackets.BuildOpusTags(null), granulePosition: 0, isEndOfStream: false);

                    var granulePosition = 0L;
                    for (var i = 0; i < packets.Count; i++)
                    {
                        granulePosition += 960;
                        pageWriter.WritePacket(packets[i], granulePosition, isEndOfStream: i == packets.Count - 1);
                    }
                }

                var decodedSamples = new List<int>();
                var streamInfo = OpusDecoder.Decode(filePath, (block, decodedChannels, rate, _, _) =>
                {
                    decodedChannels.Should().Be(channels);
                    rate.Should().Be(SampleRate);
                    decodedSamples.AddRange(block.ToArray());
                });

                streamInfo.Channels.Should().Be(channels);
                decodedSamples.Should().NotBeEmpty();

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
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
        }
    }
}
