using EggEncoder.Pcm;
using FluentAssertions;

namespace EggEncoder.UnitTests.Pcm
{
    public class PcmTransformPipelineTest
    {
        [Fact]
        public void EmptyPipeline_Should_Be_A_NoOp_Passthrough()
        {
            var pipeline = new PcmTransformPipeline();
            var buffer = new[] { 1, 2, 3, 4 };

            pipeline.HasTransforms.Should().BeFalse();

            var (outBuffer, frameCount, channels, sampleRate, bitsPerSample) =
                pipeline.Apply(buffer, frameCount: 2, channels: 2, sampleRate: 44100, bitsPerSample: 16);

            outBuffer.Should().BeSameAs(buffer);
            frameCount.Should().Be(2);
            channels.Should().Be(2);
            sampleRate.Should().Be(44100);
            bitsPerSample.Should().Be(16);
        }

        [Fact]
        public void Apply_Should_Chain_Transforms_In_Order()
        {
            var pipeline = new PcmTransformPipeline(
                new ChannelRemixTransform(inputChannels: 2, outputChannels: 1),
                new VolumeTransform(2.0));

            var buffer = new[] { 1000, 2000, 3000, 4000 }; // 2 stereo frames

            var (outBuffer, frameCount, channels, sampleRate, bitsPerSample) =
                pipeline.Apply(buffer, frameCount: 2, channels: 2, sampleRate: 44100, bitsPerSample: 16);

            channels.Should().Be(1);
            sampleRate.Should().Be(44100);
            bitsPerSample.Should().Be(16);
            frameCount.Should().Be(2);
            // Remix averages L+R (1500, 3500), then volume doubles it.
            outBuffer.Should().Equal(3000, 7000);
        }

        [Fact]
        public void ComputeOutputFormat_Should_Match_What_Apply_Would_Produce_Without_Processing_Any_Audio()
        {
            var pipeline = new PcmTransformPipeline(
                new ResamplingTransform(sourceRate: 44100, targetRate: 48000, channels: 2),
                new ChannelRemixTransform(inputChannels: 2, outputChannels: 1),
                new BitDepthFormatTransform(fromBits: 16, toBits: 24));

            var (channels, sampleRate, bitsPerSample) = pipeline.ComputeOutputFormat(inputChannels: 2, inputSampleRate: 44100, inputBitsPerSample: 16);

            channels.Should().Be(1);
            sampleRate.Should().Be(48000);
            bitsPerSample.Should().Be(24);
        }

        [Fact]
        public void ComputeOutputFormat_EmptyPipeline_Should_Return_Input_Format_Unchanged()
        {
            var pipeline = new PcmTransformPipeline();

            var (channels, sampleRate, bitsPerSample) = pipeline.ComputeOutputFormat(inputChannels: 2, inputSampleRate: 44100, inputBitsPerSample: 16);

            channels.Should().Be(2);
            sampleRate.Should().Be(44100);
            bitsPerSample.Should().Be(16);
        }

        [Fact]
        public void NoOpTransform_Should_Not_Affect_Format_Or_Values()
        {
            var pipeline = new PcmTransformPipeline(NoOpTransform.Instance);
            var buffer = new[] { 1, 2, 3 };

            var (outBuffer, frameCount, channels, sampleRate, bitsPerSample) =
                pipeline.Apply(buffer, frameCount: 3, channels: 1, sampleRate: 22050, bitsPerSample: 8);

            outBuffer.Should().Equal(1, 2, 3);
            frameCount.Should().Be(3);
            channels.Should().Be(1);
            sampleRate.Should().Be(22050);
            bitsPerSample.Should().Be(8);
        }

        [Fact]
        public void Flush_EmptyPipeline_Should_Produce_No_Output()
        {
            var pipeline = new PcmTransformPipeline();

            var (buffer, frameCount, channels, sampleRate, bitsPerSample) = pipeline.Flush(channels: 2, sampleRate: 44100, bitsPerSample: 16);

            frameCount.Should().Be(0);
            buffer.Should().BeEmpty();
            channels.Should().Be(2);
            sampleRate.Should().Be(44100);
            bitsPerSample.Should().Be(16);
        }

        [Fact]
        public void Flush_PipelineWithNoStatefulTransforms_Should_Produce_No_Output()
        {
            var pipeline = new PcmTransformPipeline(new VolumeTransform(2.0));
            pipeline.Apply([1000, 2000], frameCount: 2, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            var (buffer, frameCount, _, _, _) = pipeline.Flush(channels: 1, sampleRate: 44100, bitsPerSample: 16);

            frameCount.Should().Be(0);
            buffer.Should().BeEmpty();
        }

        [Fact]
        public void Flush_Should_Drain_A_Resamplers_Pending_Tail()
        {
            var pipeline = new PcmTransformPipeline(new ResamplingTransform(sourceRate: 1000, targetRate: 2000, channels: 1));

            var (_, applyFrameCount, _, _, _) = pipeline.Apply([1, 2, 3], frameCount: 3, channels: 1, sampleRate: 1000, bitsPerSample: 16);
            var (_, flushFrameCount, _, _, _) = pipeline.Flush(channels: 1, sampleRate: 1000, bitsPerSample: 16);

            (applyFrameCount + flushFrameCount).Should().Be(6); // round(3 * 2.0)
        }

        [Fact]
        public void Flush_Should_Push_An_Upstream_Transforms_Tail_Through_Every_Later_Transform()
        {
            // The resampler's flushed tail must itself be doubled by the VolumeTransform that follows it
            // in the pipeline -- not written out untouched. Compare against an identical pipeline minus
            // the VolumeTransform to prove the relationship, rather than asserting on raw magnitudes.
            int[] source = [100, 200, 300, 400, 500];

            var withoutVolume = new PcmTransformPipeline(new ResamplingTransform(sourceRate: 1000, targetRate: 2000, channels: 1));
            withoutVolume.Apply((int[])source.Clone(), frameCount: source.Length, channels: 1, sampleRate: 1000, bitsPerSample: 16);
            var (unscaledFlush, unscaledFlushCount, _, _, _) = withoutVolume.Flush(channels: 1, sampleRate: 1000, bitsPerSample: 16);

            var withVolume = new PcmTransformPipeline(
                new ResamplingTransform(sourceRate: 1000, targetRate: 2000, channels: 1),
                new VolumeTransform(2.0));
            withVolume.Apply((int[])source.Clone(), frameCount: source.Length, channels: 1, sampleRate: 1000, bitsPerSample: 16);
            var (scaledFlush, scaledFlushCount, _, _, _) = withVolume.Flush(channels: 1, sampleRate: 1000, bitsPerSample: 16);

            scaledFlushCount.Should().Be(unscaledFlushCount);
            scaledFlushCount.Should().BeGreaterThan(0);
            for (var i = 0; i < scaledFlushCount; i++)
            {
                scaledFlush[i].Should().Be(unscaledFlush[i] * 2);
            }
        }
    }
}
