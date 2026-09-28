using EggEncoder.Pcm;
using FluentAssertions;

namespace EggEncoder.UnitTests.Pcm
{
    public class ResamplingTransformTest
    {
        [Fact]
        public void Apply_SameRate_Should_Return_Same_Buffer_Unchanged()
        {
            var transform = new ResamplingTransform(sourceRate: 44100, targetRate: 44100, channels: 1);
            var buffer = new[] { 1, 2, 3, 4 };

            var (outBuffer, outFrameCount) = transform.Apply(buffer, frameCount: 4, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            outBuffer.Should().BeSameAs(buffer);
            outFrameCount.Should().Be(4);
        }

        [Fact]
        public void Apply_Upsampling_Should_Preserve_Exact_Values_At_Aligned_Frames()
        {
            var transform = new ResamplingTransform(sourceRate: 4, targetRate: 8, channels: 1);
            var buffer = new[] { 0, 100, 200, 300 };

            var (outBuffer, outFrameCount) = transform.Apply(buffer, frameCount: 4, channels: 1, sampleRate: 4, bitsPerSample: 16);

            outFrameCount.Should().Be(8);
            // Every even destination frame lands exactly on a source frame (frac == 0), so those values
            // are exact regardless of interpolation rounding.
            outBuffer[0].Should().Be(0);
            outBuffer[2].Should().Be(100);
            outBuffer[4].Should().Be(200);
            outBuffer[6].Should().Be(300);
        }

        [Fact]
        public void Apply_Downsampling_Should_Halve_FrameCount()
        {
            var transform = new ResamplingTransform(sourceRate: 8, targetRate: 4, channels: 1);
            var buffer = new[] { 0, 100, 200, 300, 400, 500, 600, 700 };

            var (_, outFrameCount) = transform.Apply(buffer, frameCount: 8, channels: 1, sampleRate: 8, bitsPerSample: 16);

            outFrameCount.Should().Be(4);
        }

        [Fact]
        public void Apply_AcrossMultipleBlocks_Should_Track_Total_FrameCount_Exactly()
        {
            // The fractional source position must carry across Apply() calls so the total output
            // frame count for N blocks matches processing all of their input as a single block --
            // this is what "streaming-safe frame count handling" means for a resampler that only
            // ever sees one block at a time.
            var wholeTransform = new ResamplingTransform(sourceRate: 3, targetRate: 5, channels: 1);
            var wholeBuffer = new[] { 0, 90, 180, 270, 360, 450 };
            var (_, wholeFrameCount) = wholeTransform.Apply(wholeBuffer, frameCount: 6, channels: 1, sampleRate: 3, bitsPerSample: 16);

            var streamingTransform = new ResamplingTransform(sourceRate: 3, targetRate: 5, channels: 1);
            var (_, firstFrameCount) = streamingTransform.Apply([0, 90, 180], frameCount: 3, channels: 1, sampleRate: 3, bitsPerSample: 16);
            var (_, secondFrameCount) = streamingTransform.Apply([270, 360, 450], frameCount: 3, channels: 1, sampleRate: 3, bitsPerSample: 16);

            (firstFrameCount + secondFrameCount).Should().Be(wholeFrameCount);
        }

        [Fact]
        public void Apply_AcrossMultipleBlocks_Should_Match_Single_Block_Away_From_The_Boundary()
        {
            // Destination frames that only need source samples from within the current block match
            // single-block processing exactly. (Frames right at a block boundary that would need the
            // next block's first sample to interpolate correctly instead duplicate the last available
            // sample -- a documented limitation of this streaming implementation, see ResamplingTransform.)
            var wholeTransform = new ResamplingTransform(sourceRate: 3, targetRate: 5, channels: 1);
            var wholeBuffer = new[] { 0, 90, 180, 270, 360, 450 };
            var (wholeOutput, _) = wholeTransform.Apply(wholeBuffer, frameCount: 6, channels: 1, sampleRate: 3, bitsPerSample: 16);

            var streamingTransform = new ResamplingTransform(sourceRate: 3, targetRate: 5, channels: 1);
            var (firstOutput, firstFrameCount) = streamingTransform.Apply([0, 90, 180], frameCount: 3, channels: 1, sampleRate: 3, bitsPerSample: 16);

            // Destination frames 0-3 of the first block only need source frames 0-1, both inside this
            // block, so they must be bit-for-bit identical to the single-block result.
            firstOutput.AsSpan(0, 4).ToArray().Should().Equal(wholeOutput.AsSpan(0, 4).ToArray());
            firstFrameCount.Should().Be(5);
        }

        [Fact]
        public void Reset_Should_Restart_Fractional_Position_From_Zero()
        {
            var transform = new ResamplingTransform(sourceRate: 4, targetRate: 8, channels: 1);
            var buffer = new[] { 0, 100, 200, 300 };

            var (firstRun, _) = transform.Apply((int[])buffer.Clone(), frameCount: 4, channels: 1, sampleRate: 4, bitsPerSample: 16);
            transform.Reset();
            var (secondRun, _) = transform.Apply((int[])buffer.Clone(), frameCount: 4, channels: 1, sampleRate: 4, bitsPerSample: 16);

            secondRun.Should().Equal(firstRun);
        }

        [Fact]
        public void Apply_WrongChannelCount_Should_Throw()
        {
            var transform = new ResamplingTransform(sourceRate: 44100, targetRate: 48000, channels: 2);
            var act = () => transform.Apply([1, 2, 3, 4], frameCount: 2, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Apply_WrongSourceSampleRate_Should_Throw()
        {
            var transform = new ResamplingTransform(sourceRate: 44100, targetRate: 48000, channels: 1);
            var act = () => transform.Apply([1, 2, 3], frameCount: 3, channels: 1, sampleRate: 22050, bitsPerSample: 16);

            act.Should().Throw<ArgumentException>();
        }

        [Theory]
        [InlineData(0, 48000)]
        [InlineData(44100, 0)]
        [InlineData(-1, 48000)]
        public void Constructor_NonPositiveRate_Should_Throw(int sourceRate, int targetRate)
        {
            var act = () => new ResamplingTransform(sourceRate, targetRate, channels: 1);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void OutputSampleRate_And_OutputChannels_Should_Reflect_Constructor_Arguments()
        {
            var transform = new ResamplingTransform(sourceRate: 44100, targetRate: 22050, channels: 2);

            transform.OutputSampleRate.Should().Be(22050);
            transform.OutputChannels.Should().Be(2);
            transform.OutputBitsPerSample.Should().Be(0);
        }
    }
}
