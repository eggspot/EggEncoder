using EggEncoder.Codecs.Opus;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Opus
{
    public class OpusDecoderTest
    {
        private const int SampleRate = 48000;

        [Fact]
        public void Decode_StereoFile_Should_Invoke_The_Callback_With_InterleavedSamples()
        {
            var samples = GenerateTone(seconds: 1, channels: 2);
            var filePath = Path.Combine(Path.GetTempPath(), $"opus_decoder_stereo_{Guid.NewGuid():N}.opus");
            try
            {
                using (var session = OpusEncoderSession.OpenSession(filePath, channels: 2, SampleRate, bitsPerSample: 16))
                {
                    session.WriteInterleavedSamples(samples, samples.Length / 2);
                    session.Finish();
                }

                var decoded = new List<int>();
                var streamInfo = OpusDecoder.Decode(filePath, (block, channels, sampleRate, bitsPerSample, _) =>
                {
                    channels.Should().Be(2);
                    sampleRate.Should().Be(SampleRate);
                    bitsPerSample.Should().Be(16);
                    decoded.AddRange(block.ToArray());
                });

                streamInfo.Channels.Should().Be(2);
                decoded.Should().NotBeEmpty();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Decode_Should_Invoke_The_Callback_MultipleTimes_With_Consistent_Metadata()
        {
            var samples = GenerateTone(seconds: 3, channels: 1); // several 960-sample frames
            var filePath = Path.Combine(Path.GetTempPath(), $"opus_decoder_multi_{Guid.NewGuid():N}.opus");
            try
            {
                using (var session = OpusEncoderSession.OpenSession(filePath, channels: 1, SampleRate, bitsPerSample: 16))
                {
                    session.WriteInterleavedSamples(samples, samples.Length);
                    session.Finish();
                }

                var callbackCount = 0;
                long? firstReportedTotal = null;
                var totalDecodedFrames = 0L;

                var streamInfo = OpusDecoder.Decode(filePath, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
                {
                    callbackCount++;
                    channels.Should().Be(1);
                    sampleRate.Should().Be(SampleRate);
                    bitsPerSample.Should().Be(16);

                    firstReportedTotal ??= totalSamples;
                    totalSamples.Should().Be(firstReportedTotal, "every callback should report the same, already-known total");

                    totalDecodedFrames += block.Length;
                });

                callbackCount.Should().BeGreaterThan(1);
                totalDecodedFrames.Should().Be(streamInfo.TotalSamples);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Decode_WithMissingOpusHead_Should_Throw()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"opus_decoder_no_head_{Guid.NewGuid():N}.opus");
            try
            {
                File.WriteAllBytes(filePath, []);

                var act = () => OpusDecoder.Decode(filePath, (_, _, _, _, _) => { });

                act.Should().ThrowExactly<InvalidDataException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Decode_WithMissingOpusTags_Should_Throw()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"opus_decoder_no_tags_{Guid.NewGuid():N}.opus");
            try
            {
                using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                {
                    var writer = new OggPageWriter(stream, serialNumber: 1);
                    writer.WritePacket(OpusHeaderPackets.BuildOpusHead(1, preSkip: 0, inputSampleRate: SampleRate), granulePosition: 0, isEndOfStream: true);
                }

                var act = () => OpusDecoder.Decode(filePath, (_, _, _, _, _) => { });

                act.Should().ThrowExactly<InvalidDataException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Decode_WithMalformedAudioPacket_Should_Throw()
        {
            // An empty audio packet (zero-length, after a valid OpusHead/OpusTags) makes
            // Concentus's own OpusPacketInfo.GetNumFrames/GetNumSamples return OPUS_BAD_ARG (a
            // negative error code) rather than a real frame count -- CountTotalSamples's pre-scan
            // must reject this rather than letting a negative "sample count" propagate.
            var filePath = Path.Combine(Path.GetTempPath(), $"opus_decoder_malformed_{Guid.NewGuid():N}.opus");
            try
            {
                using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                {
                    var writer = new OggPageWriter(stream, serialNumber: 1);
                    writer.WritePacket(OpusHeaderPackets.BuildOpusHead(1, preSkip: 0, inputSampleRate: SampleRate), granulePosition: 0, isEndOfStream: false);
                    writer.WritePacket(OpusHeaderPackets.BuildOpusTags(), granulePosition: 0, isEndOfStream: false);
                    writer.WritePacket([], granulePosition: 0, isEndOfStream: true);
                }

                var act = () => OpusDecoder.Decode(filePath, (_, _, _, _, _) => { });

                act.Should().ThrowExactly<InvalidDataException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Decode_WithUnsupportedChannelCount_Should_Throw()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"opus_decoder_bad_channels_{Guid.NewGuid():N}.opus");
            try
            {
                using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                {
                    var writer = new OggPageWriter(stream, serialNumber: 1);
                    var head = OpusHeaderPackets.BuildOpusHead(1, preSkip: 0, inputSampleRate: SampleRate);
                    head[9] = 5; // corrupt the channel count field directly
                    writer.WritePacket(head, granulePosition: 0, isEndOfStream: true);
                }

                var act = () => OpusDecoder.Decode(filePath, (_, _, _, _, _) => { });

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
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
    }
}
