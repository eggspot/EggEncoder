using EggEncoder.Pcm;
using FluentAssertions;

namespace EggEncoder.UnitTests.Pcm
{
    public class EchoTransformTest
    {
        // sampleRate=10, delaySeconds=0.1 => exactly 1 frame of delay, which makes every echo's
        // integer arithmetic fully hand-traceable (each frame is one "visit" to the single delay-line
        // slot) rather than needing to skip ahead a whole delayFrames-sized block to see a repeat.
        private static EchoTransform CreateSingleFrameDelayEcho(double feedback = 0.5, double wetGain = 1.0) =>
            new(sampleRate: 10, channels: 1, delaySeconds: 0.1, feedback, wetGain);

        [Fact]
        public void Apply_FirstFrame_Should_Be_Unaffected_By_An_EmptyDelayLine()
        {
            var transform = CreateSingleFrameDelayEcho();
            var buffer = new[] { 1000 };

            var (outBuffer, frameCount) = transform.Apply(buffer, frameCount: 1, channels: 1, sampleRate: 10, bitsPerSample: 16);

            frameCount.Should().Be(1);
            outBuffer[0].Should().Be(1000, "the delay line starts silent, so the very first frame has no echo yet");
        }

        [Fact]
        public void Apply_SubsequentFrames_Should_Carry_The_Delayed_And_Decaying_Repeat()
        {
            var transform = CreateSingleFrameDelayEcho(feedback: 0.5, wetGain: 1.0);
            var buffer = new[] { 1000, 0, 0, 0 };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 4, channels: 1, sampleRate: 10, bitsPerSample: 16);

            // Hand-traced: frame0 dry=1000 (no echo yet); frame1 echoes the full 1000 (wetGain=1.0);
            // frame2/3 each halve (feedback=0.5) the *stored* delay-line value from the previous visit.
            outBuffer.Should().Equal(1000, 1000, 500, 250);
        }

        [Fact]
        public void Apply_WithZeroFeedback_Should_Produce_ExactlyOneRepeat_NoFurtherEchoes()
        {
            var transform = CreateSingleFrameDelayEcho(feedback: 0.0, wetGain: 1.0);
            var buffer = new[] { 1000, 0, 0 };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 3, channels: 1, sampleRate: 10, bitsPerSample: 16);

            // feedback == 0 means the delay line holds nothing after the one repeat -- no second echo.
            outBuffer.Should().Equal(1000, 1000, 0);
        }

        [Fact]
        public void Apply_WithWetGain_Should_Scale_The_Echo_Independently_Of_The_Dry_Signal()
        {
            var transform = CreateSingleFrameDelayEcho(feedback: 0.0, wetGain: 0.25);
            var buffer = new[] { 1000, 0 };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 2, channels: 1, sampleRate: 10, bitsPerSample: 16);

            // The dry sample passes through at full level; only the echo itself is scaled by wetGain.
            outBuffer.Should().Equal(1000, 250);
        }

        [Fact]
        public void Apply_StereoLinkedDelayLine_Should_Keep_Channels_Independent()
        {
            var transform = new EchoTransform(sampleRate: 10, channels: 2, delaySeconds: 0.1, feedback: 0.5, wetGain: 1.0);
            var buffer = new[] { 1000, 2000, 0, 0 }; // frame0: ch0=1000, ch1=2000; frame1: silence

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 2, channels: 2, sampleRate: 10, bitsPerSample: 16);

            // Each channel has its own delay-line slot, so channel 1's louder input never bleeds
            // into channel 0's echo or vice versa.
            outBuffer.Should().Equal(1000, 2000, 1000, 2000);
        }

        [Fact]
        public void Apply_LargeWetGain_Should_Clamp_To_The_Current_Bit_Depths_Native_Range()
        {
            var transform = CreateSingleFrameDelayEcho(feedback: 0.0, wetGain: 100.0);
            var buffer = new[] { 1000, 0 };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 2, channels: 1, sampleRate: 10, bitsPerSample: 16);

            outBuffer[1].Should().Be(short.MaxValue, "100x wetGain on a 1000-amplitude echo would be 100000, far past 16-bit range");
        }

        [Fact]
        public void Apply_SplitAcrossMultipleCalls_Should_Produce_The_Same_Result_As_OneCall()
        {
            var samples = new[] { 1000, 0, 0, 0, 0, 0 };

            var singleCallTransform = CreateSingleFrameDelayEcho();
            var (singleCallOutput, _) = singleCallTransform.Apply(samples.ToArray(), frameCount: 6, channels: 1, sampleRate: 10, bitsPerSample: 16);

            var splitCallTransform = CreateSingleFrameDelayEcho();
            var (firstHalfOutput, _) = splitCallTransform.Apply(samples[..3], frameCount: 3, channels: 1, sampleRate: 10, bitsPerSample: 16);
            var (secondHalfOutput, _) = splitCallTransform.Apply(samples[3..], frameCount: 3, channels: 1, sampleRate: 10, bitsPerSample: 16);

            firstHalfOutput.Concat(secondHalfOutput).Should().Equal(singleCallOutput);
        }

        [Fact]
        public void Reset_Should_ClearTheDelayLine_BackToSilence()
        {
            var transform = CreateSingleFrameDelayEcho();
            transform.Apply([1000], frameCount: 1, channels: 1, sampleRate: 10, bitsPerSample: 16);

            transform.Reset();
            var (outBuffer, _) = transform.Apply([0], frameCount: 1, channels: 1, sampleRate: 10, bitsPerSample: 16);

            outBuffer[0].Should().Be(0, "the delay line should have been cleared, so this silent sample has no leftover echo from before Reset()");
        }

        [Fact]
        public void Flush_ShouldDrainTheDecayingTail_UntilItReachesSilence()
        {
            var transform = CreateSingleFrameDelayEcho(feedback: 0.5, wetGain: 1.0);
            transform.Apply([1000], frameCount: 1, channels: 1, sampleRate: 10, bitsPerSample: 16);

            var (tailBuffer, tailFrameCount) = transform.Flush();

            // Hand-traced integer recurrence (each visit halves the stored delay-line value, with
            // truncation): 1000 -> 500 -> 250 -> 125 -> 62 -> 31 -> 15 -> 7 -> 3 -> 1, then silent.
            tailFrameCount.Should().Be(10);
            tailBuffer.Take(10).Should().Equal(1000, 500, 250, 125, 62, 31, 15, 7, 3, 1);
        }

        [Fact]
        public void CanChangeFrameCount_Should_Be_True_WhenFeedbackIsNonZero()
        {
            // Apply() itself always returns exactly the frame count it was given, but Flush() then
            // adds a genuine decaying tail beyond that whenever feedback > 0 -- so a caller sizing a
            // destination sink from the input count alone (e.g. AudioCutter.Convert opening a
            // WavWriter) cannot trust that count unless this reports true, mirroring
            // ResamplingTransform's own conditional CanChangeFrameCount (_ratio != 1.0).
            var transform = CreateSingleFrameDelayEcho(feedback: 0.5);

            transform.CanChangeFrameCount.Should().BeTrue();
        }

        [Fact]
        public void CanChangeFrameCount_Should_Be_False_WhenFeedbackIsZero()
        {
            var transform = CreateSingleFrameDelayEcho(feedback: 0.0);

            transform.CanChangeFrameCount.Should().BeFalse("feedback == 0 means Flush() always returns an empty tail, so total output truly equals total input");
        }

        [Fact]
        public void Flush_WithZeroFeedback_Should_ReturnNoTail()
        {
            var transform = CreateSingleFrameDelayEcho(feedback: 0.0, wetGain: 1.0);
            transform.Apply([1000], frameCount: 1, channels: 1, sampleRate: 10, bitsPerSample: 16);

            var (tailBuffer, tailFrameCount) = transform.Flush();

            tailFrameCount.Should().Be(0);
            tailBuffer.Should().BeEmpty();
        }

        [Fact]
        public void Flush_WithoutAnyPriorApplyCall_Should_ReturnNoTail()
        {
            var transform = CreateSingleFrameDelayEcho();

            var (tailBuffer, tailFrameCount) = transform.Flush();

            tailFrameCount.Should().Be(0);
            tailBuffer.Should().BeEmpty();
        }

        [Fact]
        public void Flush_WithFeedbackVeryCloseToOne_Should_StillBeBoundedByMaxTailSeconds()
        {
            const int sampleRate = 100;
            var transform = new EchoTransform(sampleRate, channels: 1, delaySeconds: 0.01, feedback: 0.999, wetGain: 1.0);
            transform.Apply([30000], frameCount: 1, channels: 1, sampleRate, bitsPerSample: 16);

            var (_, tailFrameCount) = transform.Flush();

            tailFrameCount.Should().BeLessThanOrEqualTo((int)(EchoTransform.MaxTailSeconds * sampleRate), "a feedback value this close to 1.0 would otherwise take an enormous number of repeats to decay below threshold");
        }

        [Fact]
        public void Flush_WithFeedbackVeryCloseToOne_AndALongDelay_Should_Not_Overflow_WhenComputingTheTailLength()
        {
            // A genuinely realistic combination -- a long, slowly-decaying tail at a high sample
            // rate -- not a contrived extreme: repeatsToDecay alone reaches into the millions at
            // feedback this close to 1.0, and multiplying that by a long delayFrames (itself in the
            // millions at a high sample rate near MaxDelaySeconds) overflows int32 by several orders
            // of magnitude before it can be clamped down to the still-int-safe maxTailFrames bound.
            // These exact values were chosen (via a brute-force search, not guessed) because the
            // un-widened `repeatsToDecay * delayFrames` computation genuinely overflows int32 to a
            // NEGATIVE number for this combination -- not just a large-but-still-positive wraparound
            // that Math.Min against maxTailFrames would quietly paper over anyway.
            const int sampleRate = 192000;
            var transform = new EchoTransform(sampleRate, channels: 1, delaySeconds: EchoTransform.MaxDelaySeconds, feedback: 0.99999992, wetGain: 1.0);
            transform.Apply([30000], frameCount: 1, channels: 1, sampleRate, bitsPerSample: 16);

            (int[] tailBuffer, int tailFrameCount) result = default;
            var act = () => result = transform.Flush();

            act.Should().NotThrow();
            result.tailFrameCount.Should().Be((int)(EchoTransform.MaxTailSeconds * sampleRate), "the true repeat count is so far past the cap that MaxTailSeconds alone determines the answer");
        }

        [Fact]
        public void Apply_WithZeroFrameCount_Should_ReturnUnchanged()
        {
            var transform = CreateSingleFrameDelayEcho();
            var buffer = new[] { 1, 2, 3 };

            var (outBuffer, frameCount) = transform.Apply(buffer, frameCount: 0, channels: 1, sampleRate: 10, bitsPerSample: 16);

            outBuffer.Should().BeSameAs(buffer);
            frameCount.Should().Be(0);
        }

        [Fact]
        public void Apply_WithMismatchedSampleRate_Should_Throw()
        {
            var transform = CreateSingleFrameDelayEcho();

            var act = () => transform.Apply([1], frameCount: 1, channels: 1, sampleRate: 48000, bitsPerSample: 16);

            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Apply_WithMismatchedChannelCount_Should_Throw()
        {
            var transform = CreateSingleFrameDelayEcho();

            var act = () => transform.Apply([1, 2], frameCount: 1, channels: 2, sampleRate: 10, bitsPerSample: 16);

            act.Should().Throw<ArgumentException>();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Constructor_WithNonPositiveSampleRate_Should_Throw(int sampleRate)
        {
            var act = () => new EchoTransform(sampleRate, channels: 1, delaySeconds: 0.1, feedback: 0.5, wetGain: 1.0);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Constructor_WithNonPositiveChannels_Should_Throw(int channels)
        {
            var act = () => new EchoTransform(sampleRate: 10, channels, delaySeconds: 0.1, feedback: 0.5, wetGain: 1.0);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-0.1)]
        [InlineData(double.NaN)]
        [InlineData(EchoTransform.MaxDelaySeconds + 0.1)]
        public void Constructor_WithOutOfRangeOrNaNDelaySeconds_Should_Throw(double delaySeconds)
        {
            var act = () => new EchoTransform(sampleRate: 10, channels: 1, delaySeconds, feedback: 0.5, wetGain: 1.0);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Theory]
        [InlineData(-0.1)]
        [InlineData(1.0)]
        [InlineData(1.1)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void Constructor_WithOutOfRangeOrNonFiniteFeedback_Should_Throw(double feedback)
        {
            var act = () => new EchoTransform(sampleRate: 10, channels: 1, delaySeconds: 0.1, feedback, wetGain: 1.0);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Theory]
        [InlineData(-0.1)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void Constructor_WithNegativeOrNonFiniteWetGain_Should_Throw(double wetGain)
        {
            var act = () => new EchoTransform(sampleRate: 10, channels: 1, delaySeconds: 0.1, feedback: 0.5, wetGain);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }
    }
}
