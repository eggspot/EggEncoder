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
        public void Flush_SameRate_Should_Produce_No_Extra_Output()
        {
            var transform = new ResamplingTransform(sourceRate: 44100, targetRate: 44100, channels: 1);
            transform.Apply([1, 2, 3, 4], frameCount: 4, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            var (flushBuffer, flushFrameCount) = transform.Flush();

            flushFrameCount.Should().Be(0);
            flushBuffer.Should().BeEmpty();
        }

        [Fact]
        public void Apply_SmallBlock_Should_Withhold_Output_Until_Enough_Lookahead_Exists()
        {
            // A block far smaller than the filter's half-width can't produce any output yet -- every
            // candidate output position still needs source frames beyond what's arrived so far. This is
            // the deliberate replacement for the old linear resampler's "duplicate the edge sample"
            // shortcut: correctness over immediacy.
            var transform = new ResamplingTransform(sourceRate: 1000, targetRate: 2000, channels: 1);

            var (outBuffer, frameCount) = transform.Apply([1, 2, 3], frameCount: 3, channels: 1, sampleRate: 1000, bitsPerSample: 16);

            frameCount.Should().Be(0);
            outBuffer.Should().BeEmpty();
        }

        [Fact]
        public void Apply_ThenFlush_SmallBlock_Should_Eventually_Produce_The_Exact_Expected_Total()
        {
            var transform = new ResamplingTransform(sourceRate: 1000, targetRate: 2000, channels: 1);

            var (_, applyFrameCount) = transform.Apply([1, 2, 3], frameCount: 3, channels: 1, sampleRate: 1000, bitsPerSample: 16);
            var (_, flushFrameCount) = transform.Flush();

            (applyFrameCount + flushFrameCount).Should().Be(6); // round(3 * 2.0) == 6
        }

        [Fact]
        public void Apply_Upsampling_Should_Preserve_Exact_Values_At_Aligned_Frames()
        {
            // At cutoff == 1 (upsampling), the windowed-sinc kernel's non-center taps are (numerically,
            // to well under 0.5) zero at every aligned frame, so exact source values are still preserved
            // -- this isn't specific to linear interpolation.
            var transform = new ResamplingTransform(sourceRate: 4, targetRate: 8, channels: 1);
            var buffer = new[] { 0, 100, 200, 300 };

            var (applyBuffer, applyFrameCount) = transform.Apply(buffer, frameCount: 4, channels: 1, sampleRate: 4, bitsPerSample: 16);
            var (flushBuffer, flushFrameCount) = transform.Flush();
            var allOutput = applyBuffer.AsSpan(0, applyFrameCount).ToArray().Concat(flushBuffer.AsSpan(0, flushFrameCount).ToArray()).ToArray();

            allOutput.Should().HaveCount(8);
            allOutput[0].Should().Be(0);
            allOutput[2].Should().Be(100);
            allOutput[4].Should().Be(200);
            allOutput[6].Should().Be(300);
        }

        [Fact]
        public void Apply_And_Flush_Downsampling_Should_Produce_The_Exact_Total_FrameCount()
        {
            var transform = new ResamplingTransform(sourceRate: 8, targetRate: 4, channels: 1);
            var buffer = new[] { 0, 100, 200, 300, 400, 500, 600, 700 };

            var (_, applyFrameCount) = transform.Apply(buffer, frameCount: 8, channels: 1, sampleRate: 8, bitsPerSample: 16);
            var (_, flushFrameCount) = transform.Flush();

            (applyFrameCount + flushFrameCount).Should().Be(4);
        }

        [Fact]
        public void Apply_AcrossManySmallBlocks_Should_Match_A_Single_WholeBuffer_Call_Exactly()
        {
            // The definition of streaming correctness for this transform: splitting the exact same audio
            // into any combination of blocks (down to one frame at a time) must produce bit-for-bit the
            // same output as processing it in one call, once both are fully flushed.
            var random = new Random(1234);
            var sourceFrameCount = 500;
            var wholeBuffer = new int[sourceFrameCount];
            for (var i = 0; i < sourceFrameCount; i++)
            {
                wholeBuffer[i] = (int)(short.MaxValue * Math.Sin(2 * Math.PI * 7 * i / sourceFrameCount));
            }

            var wholeTransform = new ResamplingTransform(sourceRate: 8000, targetRate: 11025, channels: 1);
            var (wholeApplyBuffer, wholeApplyCount) = wholeTransform.Apply((int[])wholeBuffer.Clone(), sourceFrameCount, channels: 1, sampleRate: 8000, bitsPerSample: 16);
            var (wholeFlushBuffer, wholeFlushCount) = wholeTransform.Flush();
            var wholeOutput = wholeApplyBuffer.AsSpan(0, wholeApplyCount).ToArray().Concat(wholeFlushBuffer.AsSpan(0, wholeFlushCount).ToArray()).ToArray();

            var streamingTransform = new ResamplingTransform(sourceRate: 8000, targetRate: 11025, channels: 1);
            var streamedOutput = new List<int>();
            var offset = 0;
            while (offset < sourceFrameCount)
            {
                var blockSize = Math.Min(1 + random.Next(37), sourceFrameCount - offset);
                var block = wholeBuffer.AsSpan(offset, blockSize).ToArray();
                var (outBuffer, outFrameCount) = streamingTransform.Apply(block, blockSize, channels: 1, sampleRate: 8000, bitsPerSample: 16);
                streamedOutput.AddRange(outBuffer.AsSpan(0, outFrameCount).ToArray());
                offset += blockSize;
            }

            var (streamFlushBuffer, streamFlushCount) = streamingTransform.Flush();
            streamedOutput.AddRange(streamFlushBuffer.AsSpan(0, streamFlushCount).ToArray());

            streamedOutput.Should().Equal(wholeOutput);
        }

        [Fact]
        public void Reset_Should_Restart_All_CrossBlock_State()
        {
            var transform = new ResamplingTransform(sourceRate: 4, targetRate: 8, channels: 1);
            var buffer = new[] { 0, 100, 200, 300 };

            var (firstApply, firstApplyCount) = transform.Apply((int[])buffer.Clone(), frameCount: 4, channels: 1, sampleRate: 4, bitsPerSample: 16);
            var (firstFlush, firstFlushCount) = transform.Flush();
            var firstRun = firstApply.AsSpan(0, firstApplyCount).ToArray().Concat(firstFlush.AsSpan(0, firstFlushCount).ToArray()).ToArray();

            transform.Reset();

            var (secondApply, secondApplyCount) = transform.Apply((int[])buffer.Clone(), frameCount: 4, channels: 1, sampleRate: 4, bitsPerSample: 16);
            var (secondFlush, secondFlushCount) = transform.Flush();
            var secondRun = secondApply.AsSpan(0, secondApplyCount).ToArray().Concat(secondFlush.AsSpan(0, secondFlushCount).ToArray()).ToArray();

            secondRun.Should().Equal(firstRun);
        }

        [Fact]
        public void Apply_AfterFlush_Should_Throw()
        {
            var transform = new ResamplingTransform(sourceRate: 1000, targetRate: 2000, channels: 1);
            transform.Apply([1, 2, 3, 4], frameCount: 4, channels: 1, sampleRate: 1000, bitsPerSample: 16);
            transform.Flush();

            var act = () => transform.Apply([5, 6], frameCount: 2, channels: 1, sampleRate: 1000, bitsPerSample: 16);

            act.Should().Throw<InvalidOperationException>();
        }

        [Fact]
        public void Flush_CalledTwice_Should_Be_Idempotent()
        {
            var transform = new ResamplingTransform(sourceRate: 1000, targetRate: 2000, channels: 1);
            transform.Apply([1, 2, 3, 4], frameCount: 4, channels: 1, sampleRate: 1000, bitsPerSample: 16);

            transform.Flush();
            var act = () => transform.Flush();

            act.Should().NotThrow();
            var (secondFlushBuffer, secondFlushFrameCount) = transform.Flush();
            secondFlushFrameCount.Should().Be(0);
            secondFlushBuffer.Should().BeEmpty();
        }

        [Fact]
        public void Flush_WithNoInputEverApplied_Should_Produce_Nothing()
        {
            var transform = new ResamplingTransform(sourceRate: 1000, targetRate: 2000, channels: 1);

            var (flushBuffer, flushFrameCount) = transform.Flush();

            flushFrameCount.Should().Be(0);
            flushBuffer.Should().BeEmpty();
        }

        [Fact]
        public void Downsampling_Should_Attenuate_A_Tone_Above_The_Target_Nyquist_Far_More_Than_One_Below_It()
        {
            // Anti-aliasing correctness: resampling 48000 -> 16000 (target Nyquist 8000 Hz) must suppress
            // a 15000 Hz tone (well above the target Nyquist, would alias into the audible band as a
            // false ~1000 Hz tone if not filtered) far more than it suppresses a 2000 Hz tone comfortably
            // inside the passband. A naive linear-interpolation resampler applies essentially no such
            // filtering -- this exercises the anti-aliasing behavior the linear implementation lacked.
            const int sourceRate = 48000;
            const int targetRate = 16000;
            const int sourceFrameCount = 4800; // 100 ms, several periods of both test tones

            var passbandOutputRms = ResampleToneAndMeasureRms(sourceRate, targetRate, sourceFrameCount, toneHz: 2000);
            var aliasingOutputRms = ResampleToneAndMeasureRms(sourceRate, targetRate, sourceFrameCount, toneHz: 15000);

            // The near-Nyquist alias candidate should be suppressed by at least ~20 dB (a factor of 10 in
            // RMS amplitude) relative to the passband tone of the same input amplitude.
            aliasingOutputRms.Should().BeLessThan(passbandOutputRms / 10.0);
        }

        private static double ResampleToneAndMeasureRms(int sourceRate, int targetRate, int sourceFrameCount, double toneHz)
        {
            var buffer = new int[sourceFrameCount];
            for (var i = 0; i < sourceFrameCount; i++)
            {
                buffer[i] = (int)(short.MaxValue * Math.Sin(2 * Math.PI * toneHz * i / sourceRate));
            }

            var transform = new ResamplingTransform(sourceRate, targetRate, channels: 1);
            var (applyBuffer, applyCount) = transform.Apply(buffer, sourceFrameCount, channels: 1, sourceRate, bitsPerSample: 16);
            var (flushBuffer, flushCount) = transform.Flush();
            var output = applyBuffer.AsSpan(0, applyCount).ToArray().Concat(flushBuffer.AsSpan(0, flushCount).ToArray()).ToArray();

            // Skip the filter's transient at the very start of the stream (its first FilterHalfWidth-ish
            // output frames still see clamped edge taps, not steady-state filtering).
            var steadyState = output.Skip(output.Length / 4).ToArray();
            var sumSquares = steadyState.Sum(s => (double)s * s);
            return Math.Sqrt(sumSquares / steadyState.Length);
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
        public void Constructor_NonPositiveFilterHalfWidth_Should_Throw()
        {
            var act = () => new ResamplingTransform(44100, 48000, channels: 1, filterHalfWidth: 0);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Constructor_FilterHalfWidthAboveMax_Should_Throw()
        {
            // Regression test: on the cutoff >= 1.0 (no downsampling scale-up) path, filterHalfWidth was
            // previously used unclamped, so an arbitrarily large caller-supplied value flowed straight
            // into the polyphase table allocation -- an extreme value could even overflow the internal
            // int kernel-length computation. Must be rejected at the constructor boundary instead.
            var act = () => new ResamplingTransform(44100, 48000, channels: 1, filterHalfWidth: 257);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Constructor_ExtremeSourceToTargetRateRatio_Should_Cap_Without_Producing_A_Garbage_HalfWidth()
        {
            // Regression test: filterHalfWidth / cutoff can exceed int range by orders of magnitude for
            // an extreme ratio (e.g. a near-int.MaxValue source rate downsampled to a tiny target rate),
            // even at the *default* filterHalfWidth. Narrowing that huge double to int before clamping
            // (instead of clamping in double first) risks an unchecked cast producing a negative or
            // otherwise garbage value that could slip past the MaxEffectiveHalfWidth clamp.
            var transform = new ResamplingTransform(sourceRate: int.MaxValue - 1, targetRate: 1, channels: 1);

            transform.FilterHalfWidth.Should().Be(256);
        }

        [Fact]
        public void Constructor_ExtremeDownsamplingRatio_Should_Cap_The_Effective_Filter_HalfWidth()
        {
            // Without a cap, a 1000:1 downsampling ratio would ask for a half-width in the tens of
            // thousands of taps -- unbounded memory and per-sample cost for a pathological ratio.
            var transform = new ResamplingTransform(sourceRate: 100_000, targetRate: 100, channels: 1);

            transform.FilterHalfWidth.Should().BeLessThanOrEqualTo(256);
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
            var transform = new ResamplingTransform(sourceRate: 2, targetRate: 4, channels: 1);
            var buffer = new[] { int.MinValue, int.MaxValue, int.MinValue, int.MaxValue };

            var (applyBuffer, applyCount) = transform.Apply(buffer, frameCount: 4, channels: 1, sampleRate: 2, bitsPerSample: 32);
            var (flushBuffer, flushCount) = transform.Flush();
            var output = applyBuffer.AsSpan(0, applyCount).ToArray().Concat(flushBuffer.AsSpan(0, flushCount).ToArray()).ToArray();

            output.Should().HaveCount(8);
            output.Should().OnlyContain(v => v >= int.MinValue && v <= int.MaxValue);
        }

        [Fact]
        public void Apply_ZeroFrameCount_Should_Return_Empty_Without_Throwing()
        {
            var transform = new ResamplingTransform(sourceRate: 44100, targetRate: 48000, channels: 1);

            var act = () => transform.Apply([], frameCount: 0, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            act.Should().NotThrow();
        }

        [Fact]
        public void Apply_MultiChannel_Should_Resample_Each_Channel_Independently()
        {
            var transform = new ResamplingTransform(sourceRate: 4, targetRate: 8, channels: 2);
            // Channel 0 ramps up, channel 1 ramps down.
            var buffer = new[] { 0, 300, 100, 200, 200, 100, 300, 0 };

            var (applyBuffer, applyCount) = transform.Apply(buffer, frameCount: 4, channels: 2, sampleRate: 4, bitsPerSample: 16);
            var (flushBuffer, flushCount) = transform.Flush();
            var output = applyBuffer.AsSpan(0, applyCount * 2).ToArray().Concat(flushBuffer.AsSpan(0, flushCount * 2).ToArray()).ToArray();

            (applyCount + flushCount).Should().Be(8);
            // Aligned frames reproduce the exact source values, per channel.
            output[0].Should().Be(0);
            output[1].Should().Be(300);
            output[4].Should().Be(100); // dst frame 2 aligns exactly with src frame 1 (100, 200)
            output[5].Should().Be(200);
        }
    }
}
