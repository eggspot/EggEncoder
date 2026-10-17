using EggEncoder.Codecs.Aac;
using EggEncoder.Streaming;
using EggEncoder.Streaming.Aac;
using FluentAssertions;

namespace EggEncoder.UnitTests.Streaming.Aac
{
    public class AacStreamingEncoderTest
    {
        private const int SampleRate = 44100;

        [Fact]
        public void Create_WithValidMonoParameters_Should_ExposeChannelsSampleRateAndFrameSize()
        {
            using var encoder = AacStreamingEncoder.Create(channels: 1, SampleRate, _ => { });

            encoder.Channels.Should().Be(1);
            encoder.SampleRate.Should().Be(SampleRate);
            encoder.SamplesPerFrame.Should().Be(1024);
        }

        [Fact]
        public void Create_WithNullCallback_Should_Throw()
        {
            var act = () => AacStreamingEncoder.Create(channels: 1, SampleRate, null!);

            act.Should().ThrowExactly<ArgumentNullException>();
        }

        [Fact]
        public void Create_WithStereoChannels_Should_Throw()
        {
            var act = () => AacStreamingEncoder.Create(channels: 2, SampleRate, _ => { });

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void Create_WithUnsupportedSampleRate_Should_Throw()
        {
            var act = () => AacStreamingEncoder.Create(channels: 1, sampleRate: 12345, _ => { });

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void Write_Should_EmitFrames_Matching_AacEncoder_RawAccessUnits_ByteForByte()
        {
            var samples = BuildToneSamples(SampleRate, 2 * SampleRate);

            var outputPath = Path.Combine(Path.GetTempPath(), $"aac_streaming_parity_{Guid.NewGuid():N}.aac");
            try
            {
                AacEncoder.Encode(outputPath, samples, channels: 1, sampleRate: SampleRate);
                var referenceBytes = File.ReadAllBytes(outputPath);

                var sampleRateIndex = AacFrameEncoder.ValidateAndGetSampleRateIndex(1, SampleRate);
                var reconstructed = new List<byte>();

                using var encoder = AacStreamingEncoder.Create(channels: 1, SampleRate, frame =>
                {
                    reconstructed.AddRange(AdtsFrameWriter.Wrap(frame.Payload.ToArray(), sampleRateIndex, channels: 1));
                });

                var interleaved = Array.ConvertAll(samples, s => (int)s);
                encoder.Write(interleaved);
                encoder.Complete();

                reconstructed.Should().Equal(referenceBytes, "the streaming encoder's raw access units, re-wrapped in ADTS, must reproduce AacEncoder.Encode's own file byte-for-byte");
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
        public void Write_AcrossMultipleCalls_Should_BufferAcrossCallBoundaries()
        {
            var samples = BuildToneSamples(SampleRate, 1100);
            var frames = new List<(int SampleCount, long FrameIndex)>();

            using var encoder = AacStreamingEncoder.Create(channels: 1, SampleRate, frame =>
                frames.Add((frame.SampleCount, frame.FrameIndex)));

            var interleaved = Array.ConvertAll(samples, s => (int)s);
            encoder.Write(interleaved.AsSpan(0, 500));
            frames.Should().BeEmpty("fewer than one full frame (1024 samples) has been written so far");

            encoder.Write(interleaved.AsSpan(500, 600));
            frames.Should().ContainSingle(f => f.SampleCount == 1024 && f.FrameIndex == 0, "the 1024th sample arrives partway through this call");
        }

        [Fact]
        public void Complete_WithNoSamplesWritten_Should_EmitNoFrames()
        {
            var emitted = false;

            using var encoder = AacStreamingEncoder.Create(channels: 1, SampleRate, _ => emitted = true);
            encoder.Complete();

            emitted.Should().BeFalse();
        }

        [Fact]
        public void Complete_WithPartialFrame_Should_EmitOneShortFinalFrame_WithCorrectSampleCount()
        {
            var samples = BuildToneSamples(SampleRate, 300);
            var frames = new List<int>();

            using var encoder = AacStreamingEncoder.Create(channels: 1, SampleRate, frame => frames.Add(frame.SampleCount));

            encoder.Write(Array.ConvertAll(samples, s => (int)s));
            frames.Should().BeEmpty();

            encoder.Complete();

            frames.Should().Equal(300);
        }

        [Fact]
        public void Complete_CalledTwice_Should_BeIdempotent_AndNotDoubleEmit()
        {
            var samples = BuildToneSamples(SampleRate, 300);
            var emitCount = 0;

            using var encoder = AacStreamingEncoder.Create(channels: 1, SampleRate, _ => emitCount++);
            encoder.Write(Array.ConvertAll(samples, s => (int)s));

            encoder.Complete();
            encoder.Complete();

            emitCount.Should().Be(1);
        }

        [Fact]
        public void Write_AfterComplete_Should_Throw()
        {
            using var encoder = AacStreamingEncoder.Create(channels: 1, SampleRate, _ => { });
            encoder.Complete();

            var act = () => encoder.Write(new[] { 1, 2, 3 });

            act.Should().ThrowExactly<InvalidOperationException>();
        }

        [Fact]
        public void FrameIndex_Should_IncrementMonotonically_AcrossMultipleFrames()
        {
            var samples = BuildToneSamples(SampleRate, 1024 * 3 + 100);
            var frameIndices = new List<long>();

            using var encoder = AacStreamingEncoder.Create(channels: 1, SampleRate, frame => frameIndices.Add(frame.FrameIndex));

            encoder.Write(Array.ConvertAll(samples, s => (int)s));
            encoder.Complete();

            frameIndices.Should().Equal(0L, 1L, 2L, 3L);
        }

        [Fact]
        public void Dispose_CalledTwice_Should_BeSafe()
        {
            var encoder = AacStreamingEncoder.Create(channels: 1, SampleRate, _ => { });

            encoder.Dispose();
            var act = () => encoder.Dispose();

            act.Should().NotThrow();
        }

        [Fact]
        public void Dispose_WithoutCallingComplete_Should_BeSafe()
        {
            var encoder = AacStreamingEncoder.Create(channels: 1, SampleRate, _ => { });
            encoder.Write(new[] { 1, 2, 3 });

            var act = () => encoder.Dispose();

            act.Should().NotThrow();
        }

        private static short[] BuildToneSamples(int sampleRate, int sampleCount)
        {
            var samples = new short[sampleCount];
            for (var i = 0; i < sampleCount; i++)
            {
                samples[i] = (short)(10000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
            }

            return samples;
        }
    }
}
