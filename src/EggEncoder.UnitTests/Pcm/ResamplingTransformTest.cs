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
        public void Apply_SecondBlock_Should_Continue_Interpolating_Not_Collapse_To_A_Duplicated_Flat_Sample()
        {
            // Regression test: the carried-over fractional position must be rebased relative to the next
            // block's zero-based indexing. Without that rebase, the position carried into the second block
            // is already past every valid index in the new (zero-based) buffer, so every destination frame
            // falls into the "past end of block" branch -- collapsing the entire second block (and every
            // block after it, in a real multi-block file) to one repeated flat sample.
            var wholeTransform = new ResamplingTransform(sourceRate: 3, targetRate: 5, channels: 1);
            var wholeBuffer = new[] { 0, 90, 180, 270, 360, 450 };
            var (wholeOutput, wholeFrameCount) = wholeTransform.Apply(wholeBuffer, frameCount: 6, channels: 1, sampleRate: 3, bitsPerSample: 16);

            var streamingTransform = new ResamplingTransform(sourceRate: 3, targetRate: 5, channels: 1);
            var (_, firstFrameCount) = streamingTransform.Apply([0, 90, 180], frameCount: 3, channels: 1, sampleRate: 3, bitsPerSample: 16);
            var (secondOutput, secondFrameCount) = streamingTransform.Apply([270, 360, 450], frameCount: 3, channels: 1, sampleRate: 3, bitsPerSample: 16);

            // The second block must reproduce exactly the whole-buffer run's remaining frames -- not a
            // single duplicated value (the bug's signature).
            var expectedSecondBlock = wholeOutput.AsSpan(firstFrameCount, secondFrameCount).ToArray();
            secondOutput.AsSpan(0, secondFrameCount).ToArray().Should().Equal(expectedSecondBlock);
            (firstFrameCount + secondFrameCount).Should().Be(wholeFrameCount);
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

        [Fact]
        public void CanChangeFrameCount_Should_Be_False_When_Rates_Are_Equal()
        {
            new ResamplingTransform(sourceRate: 44100, targetRate: 44100, channels: 1).CanChangeFrameCount.Should().BeFalse();
            new ResamplingTransform(sourceRate: 44100, targetRate: 48000, channels: 1).CanChangeFrameCount.Should().BeTrue();
        }

        [Fact]
        public void Apply_InterpolatingBetween_Extreme_32Bit_Values_Should_Not_Overflow()
        {
            // Regression test: (v1 - v0) computed in unchecked int arithmetic before widening to double
            // overflows for widely-separated 32-bit-depth samples, wrapping to a small/wrong value and
            // collapsing the interpolation toward v0 instead of ramping toward v1.
            var transform = new ResamplingTransform(sourceRate: 2, targetRate: 4, channels: 1);
            var buffer = new[] { int.MinValue, int.MaxValue };

            var (outBuffer, frameCount) = transform.Apply(buffer, frameCount: 2, channels: 1, sampleRate: 2, bitsPerSample: 32);

            frameCount.Should().Be(4);
            outBuffer[0].Should().Be(int.MinValue); // frac == 0, exact
            // dst1 lands halfway between int.MinValue and int.MaxValue -- the true midpoint is ~0, not
            // collapsed toward int.MinValue by an overflowed difference.
            outBuffer[1].Should().BeInRange(-2, 2);
        }

        [Fact]
        public void Apply_HeavyDownsampling_Should_Allow_Zero_Output_Frames_For_A_Block()
        {
            // A block whose rounded contribution is legitimately 0 must produce 0 frames, not a forced
            // minimum of 1 -- otherwise splitting a stream into blocks can produce more total output than
            // processing it as a single block would, breaking the documented frame-count guarantee.
            var transform = new ResamplingTransform(sourceRate: 100, targetRate: 1, channels: 1);

            var (outBuffer, frameCount) = transform.Apply([1, 2, 3], frameCount: 3, channels: 1, sampleRate: 100, bitsPerSample: 16);

            frameCount.Should().Be(0); // ratio 0.01 * 3 frames = 0.03, rounds to 0
            outBuffer.Should().BeEmpty();
        }

        [Fact]
        public void Apply_ZeroFrameCount_Should_Return_Empty_Without_Throwing()
        {
            var transform = new ResamplingTransform(sourceRate: 44100, targetRate: 48000, channels: 1);

            var act = () => transform.Apply([], frameCount: 0, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            act.Should().NotThrow();
        }
    }
}
