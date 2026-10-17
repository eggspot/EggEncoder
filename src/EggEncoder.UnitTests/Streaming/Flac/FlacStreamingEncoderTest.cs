using EggEncoder.Codecs.Flac;
using EggEncoder.Streaming;
using EggEncoder.Streaming.Flac;
using EggEncoder.Transform;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Streaming.Flac
{
    public class FlacStreamingEncoderTest
    {
        private const int SampleRate = 44100;
        private const int BitsPerSample = 16;

        [Fact]
        public void Create_WithNullCallback_Should_Throw()
        {
            var act = () => FlacStreamingEncoder.Create(1, SampleRate, BitsPerSample, null!);

            act.Should().ThrowExactly<ArgumentNullException>();
        }

        [Fact]
        public void Create_WithZeroChannels_Should_Throw()
        {
            var act = () => FlacStreamingEncoder.Create(0, SampleRate, BitsPerSample, _ => { });

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(33)]
        public void Create_WithOutOfRangeBitsPerSample_Should_Throw(int bitsPerSample)
        {
            var act = () => FlacStreamingEncoder.Create(1, SampleRate, bitsPerSample, _ => { });

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void Create_WithValidParameters_Should_ExposeChannelsSampleRateAndFrameSize()
        {
            using var encoder = FlacStreamingEncoder.Create(2, SampleRate, BitsPerSample, _ => { });

            encoder.Channels.Should().Be(2);
            encoder.SampleRate.Should().Be(SampleRate);
            encoder.SamplesPerFrame.Should().Be(4096);
        }

        [Fact]
        public void Complete_WithNoSamplesWritten_Should_EmitNoFrames()
        {
            var emitted = false;
            using var encoder = FlacStreamingEncoder.Create(1, SampleRate, BitsPerSample, _ => emitted = true);

            encoder.Complete();

            emitted.Should().BeFalse();
        }

        [Fact]
        public void Complete_CalledTwice_Should_BeIdempotent()
        {
            var emitCount = 0;
            using var encoder = FlacStreamingEncoder.Create(1, SampleRate, BitsPerSample, _ => emitCount++);
            encoder.Write(GenerateToneFrames(frameCount: 100, channels: 1));

            encoder.Complete();
            var countAfterFirstComplete = emitCount;
            encoder.Complete();

            emitCount.Should().Be(countAfterFirstComplete);
        }

        [Fact]
        public void Write_AfterComplete_Should_Throw()
        {
            using var encoder = FlacStreamingEncoder.Create(1, SampleRate, BitsPerSample, _ => { });
            encoder.Complete();

            var act = () => encoder.Write(new[] { 1, 2, 3 });

            act.Should().ThrowExactly<InvalidOperationException>();
        }

        [Fact]
        public void Dispose_CalledTwice_Should_BeSafe()
        {
            var encoder = FlacStreamingEncoder.Create(1, SampleRate, BitsPerSample, _ => { });

            encoder.Dispose();
            var act = encoder.Dispose;

            act.Should().NotThrow();
        }

        [Fact]
        public void Dispose_WithoutCallingComplete_Should_BeSafe()
        {
            var encoder = FlacStreamingEncoder.Create(1, SampleRate, BitsPerSample, _ => { });
            encoder.Write(new[] { 1, 2, 3 });

            var act = encoder.Dispose;

            act.Should().NotThrow();
        }

        [Fact]
        public void FrameIndex_Should_IncrementMonotonically()
        {
            var frameIndices = new List<long>();
            using var encoder = FlacStreamingEncoder.Create(1, SampleRate, BitsPerSample, frame => frameIndices.Add(frame.FrameIndex));

            encoder.Write(GenerateToneFrames(frameCount: 4096 * 3 + 100, channels: 1));
            encoder.Complete();

            frameIndices.Should().Equal(Enumerable.Range(0, frameIndices.Count).Select(i => (long)i));
        }

        [Fact]
        public void Complete_WithPartialBlock_Should_ReportTrueShortSampleCount_WithNoPadding()
        {
            var sampleCounts = new List<int>();
            using var encoder = FlacStreamingEncoder.Create(1, SampleRate, BitsPerSample, frame => sampleCounts.Add(frame.SampleCount));

            encoder.Write(GenerateToneFrames(frameCount: 300, channels: 1)); // less than one 4096-sample block
            sampleCounts.Should().BeEmpty();

            encoder.Complete();

            sampleCounts.Should().Equal(new[] { 300 }, "FLAC's own frame format allows a genuinely short final block natively -- no padding and no separate flush frame, unlike AAC-LC/Opus/MP3");
        }

        [Fact]
        public void Complete_WithExactlyOneFullBlock_Should_EmitExactlyOneFrame_AndNothingElse()
        {
            var sampleCounts = new List<int>();
            using var encoder = FlacStreamingEncoder.Create(1, SampleRate, BitsPerSample, frame => sampleCounts.Add(frame.SampleCount));

            encoder.Write(GenerateToneFrames(frameCount: 4096, channels: 1));
            sampleCounts.Should().Equal(4096);

            encoder.Complete();

            sampleCounts.Should().Equal(new[] { 4096 }, "FLAC has no lookahead to drain (unlike Opus/MP3), so nothing pending means Complete() emits nothing further");
        }

        [Fact]
        public void Mono_Should_RoundTrip_Exactly()
        {
            AssertExactRoundTrip(GenerateToneFrames(frameCount: 10000, channels: 1), channels: 1);
        }

        [Fact]
        public void Stereo_Should_RoundTrip_Exactly()
        {
            AssertExactRoundTrip(GenerateToneFrames(frameCount: 10000, channels: 2), channels: 2);
        }

        private static void AssertExactRoundTrip(int[] originalInterleaved, int channels)
        {
            var frameBytes = new List<byte[]>();
            using (var encoder = FlacStreamingEncoder.Create(channels, SampleRate, BitsPerSample, frame => frameBytes.Add(frame.Payload.ToArray())))
            {
                encoder.Write(originalInterleaved);
                encoder.Complete();
            }

            var filePath = Path.Combine(Path.GetTempPath(), $"flac_streaming_{Guid.NewGuid():N}.flac");
            try
            {
                File.WriteAllBytes(filePath, BuildFlacFile(frameBytes, channels, BitsPerSample, SampleRate));

                var (streamInfo, decodedSamples) = FlacTestDecoder.DecodeAll(filePath);

                streamInfo.Channels.Should().Be(channels);
                streamInfo.SampleRate.Should().Be(SampleRate);
                decodedSamples.Should().Equal(originalInterleaved, "FLAC is lossless -- the decoded samples must match exactly, not just approximately");
            }
            finally
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
        }

        // Builds a complete, real, decodable FLAC file from this encoder's own raw frames: a real
        // STREAMINFO block out-of-band (reusing FlacEncoderSession's own exact bit layout via its
        // now-internal WriteStreamInfoPayload, rather than duplicating it) followed by the frames
        // themselves -- the construction this type's own doc comment says a caller must do, since
        // (unlike AacStreamingEncoder/OpusStreamingEncoder/Mp3StreamingEncoder) these raw frames are
        // not independently decodable on their own. totalSamples=0/an all-zero MD5 are both safe
        // placeholders here: confirmed directly from FlacDecoder's own decode loop, which reads
        // frames until end-of-stream regardless of the declared total (only using it for a
        // post-hoc consistency check skipped entirely when it's 0), and never reads MD5 at all.
        private static byte[] BuildFlacFile(IReadOnlyList<byte[]> frames, int channels, int bitsPerSample, int sampleRate)
        {
            var writer = new BitWriter();
            writer.WriteBits('f', 8);
            writer.WriteBits('L', 8);
            writer.WriteBits('a', 8);
            writer.WriteBits('C', 8);
            writer.WriteBits(1, 1); // isLast -- no other metadata block follows
            writer.WriteBits(0, 7); // block type 0: STREAMINFO
            writer.WriteBits(34, 24);
            FlacEncoderSession.WriteStreamInfoPayload(writer, channels, bitsPerSample, sampleRate, minBlockSize: 4096, maxBlockSize: 4096, totalSamples: 0, md5: new byte[16]);

            return writer.ToArray().Concat(frames.SelectMany(f => f)).ToArray();
        }

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
    }
}
