using EggEncoder.Pcm;
using FluentAssertions;

namespace EggEncoder.UnitTests.Pcm
{
    public class CompressorTransformTest
    {
        [Fact]
        public void Apply_BelowThreshold_Should_Leave_Samples_Unchanged()
        {
            // -6dBFS of 16-bit full scale is ~16422; 100 is nowhere near it, even once the envelope
            // (instant attack/release here) snaps directly to the instant peak.
            var transform = new CompressorTransform(sampleRate: 44100, thresholdDb: -6, ratio: 4, attackMs: 0, releaseMs: 0);
            var buffer = new[] { 100, -100, 50 };

            var (outBuffer, frameCount) = transform.Apply(buffer, frameCount: 3, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            frameCount.Should().Be(3);
            outBuffer.Should().Equal(100, -100, 50);
        }

        [Fact]
        public void Apply_AboveThreshold_WithInstantAttack_Should_Apply_The_Computed_Reduction()
        {
            const double thresholdDb = -6;
            const double ratio = 4;
            var transform = new CompressorTransform(sampleRate: 44100, thresholdDb, ratio, attackMs: 0, releaseMs: 0);
            var buffer = new[] { (int)short.MaxValue };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 1, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            // Instant attack -> envelope snaps directly to the full-scale instant peak -> envelopeDb = 0.
            var reductionDb = (0 - thresholdDb) * (1.0 - (1.0 / ratio));
            var expectedGain = Math.Pow(10.0, -reductionDb / 20.0);
            var expected = (int)Math.Clamp(short.MaxValue * expectedGain, short.MinValue, short.MaxValue);
            outBuffer[0].Should().Be(expected);
        }

        [Fact]
        public void Apply_RatioOfOne_Should_Never_Reduce_EvenAboveThreshold()
        {
            var transform = new CompressorTransform(sampleRate: 44100, thresholdDb: -6, ratio: 1.0, attackMs: 0, releaseMs: 0);
            var buffer = new[] { (int)short.MaxValue };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 1, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            outBuffer[0].Should().Be(short.MaxValue, "a ratio of 1.0 means 1-1/ratio == 0, so reductionDb is always 0 regardless of level");
        }

        [Fact]
        public void Apply_VeryHighRatio_Should_Approach_But_Match_The_Computed_NearLimiting_Reduction()
        {
            const double thresholdDb = -6;
            const double ratio = 1000;
            var transform = new CompressorTransform(sampleRate: 44100, thresholdDb, ratio, attackMs: 0, releaseMs: 0);
            var buffer = new[] { (int)short.MaxValue };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 1, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            // At a very high ratio, 1-1/ratio approaches 1, so reductionDb approaches the full
            // (envelopeDb - thresholdDb) -- i.e. output approaches the threshold level itself, the
            // defining behavior of a limiter.
            var reductionDb = (0 - thresholdDb) * (1.0 - (1.0 / ratio));
            var expectedGain = Math.Pow(10.0, -reductionDb / 20.0);
            var expected = (int)Math.Clamp(short.MaxValue * expectedGain, short.MinValue, short.MaxValue);
            outBuffer[0].Should().Be(expected);
            reductionDb.Should().BeApproximately(6.0, 0.01, "a 1000:1 ratio should reduce nearly the full 6dB the signal sits above threshold");
        }

        [Fact]
        public void Apply_MakeupGain_Should_Apply_FlatGain_RegardlessOfThreshold()
        {
            const double makeupGainDb = 6.0;
            var transform = new CompressorTransform(sampleRate: 44100, thresholdDb: -6, ratio: 4, attackMs: 0, releaseMs: 0, makeupGainDb);
            var buffer = new[] { 100 }; // well below threshold -> no gain reduction, only makeup gain applies

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 1, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            var expectedGain = Math.Pow(10.0, makeupGainDb / 20.0);
            outBuffer[0].Should().Be((int)Math.Clamp(100 * expectedGain, short.MinValue, short.MaxValue));
        }

        [Fact]
        public void Apply_StereoLinkedEnvelope_Should_Reduce_BothChannels_Equally_WhenOnlyOneChannelIsLoud()
        {
            var transform = new CompressorTransform(sampleRate: 44100, thresholdDb: -6, ratio: 4, attackMs: 0, releaseMs: 0);
            var buffer = new[] { short.MaxValue, 0 }; // channel 0 full-scale, channel 1 silent

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 1, channels: 2, sampleRate: 44100, bitsPerSample: 16);

            outBuffer[0].Should().NotBe(short.MaxValue, "the loud channel should be reduced");
            outBuffer[1].Should().Be(0, "a silent sample times any finite gain is still silent");

            // Confirm the gain actually applied (derived from channel 0's own before/after ratio)
            // matches the single-channel formula -- proving the envelope used the loud channel's
            // peak, not some average/other combination across channels. Tolerance is wider than a
            // pure floating-point comparison would need, since outBuffer[0] has already been
            // rounded down to a whole 16-bit sample -- one LSB out of 32767 is itself ~3e-5.
            var appliedGain = (double)outBuffer[0] / short.MaxValue;
            const double thresholdDb = -6;
            const double ratio = 4;
            var reductionDb = (0 - thresholdDb) * (1.0 - (1.0 / ratio));
            var expectedGain = Math.Pow(10.0, -reductionDb / 20.0);
            appliedGain.Should().BeApproximately(expectedGain, 1e-4);
        }

        [Fact]
        public void Apply_WithNonZeroAttack_FirstSample_Should_Remain_Unchanged()
        {
            // After just one step, the envelope has only moved a tiny fraction of the way toward the
            // full-scale peak -- nowhere near crossing the -6dBFS threshold yet, so there's no
            // reduction at all on the very first sample despite the input being full-scale.
            var transform = new CompressorTransform(sampleRate: 1000, thresholdDb: -6, ratio: 4, attackMs: 100, releaseMs: 100);
            var buffer = new[] { (int)short.MaxValue };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 1, channels: 1, sampleRate: 1000, bitsPerSample: 16);

            outBuffer[0].Should().Be(short.MaxValue);
        }

        [Fact]
        public void Apply_WithNonZeroAttack_Should_EventuallyReduce_AfterManySamples()
        {
            var transform = new CompressorTransform(sampleRate: 1000, thresholdDb: -6, ratio: 4, attackMs: 100, releaseMs: 100);
            var buffer = Enumerable.Repeat((int)short.MaxValue, 500).ToArray();

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 500, channels: 1, sampleRate: 1000, bitsPerSample: 16);

            outBuffer[0].Should().Be(short.MaxValue, "no reduction yet on the very first sample");
            outBuffer[^1].Should().BeLessThan(short.MaxValue, "the envelope should have caught up and triggered reduction well before 500 samples (100ms attack at 1000Hz = 100 samples)");
        }

        [Fact]
        public void Apply_SplitAcrossMultipleCalls_Should_Produce_The_Same_Result_As_OneCall()
        {
            // Mirrors FadeTransformTest's/WaveformCalculatorTest's own split-vs-single-call
            // consistency tests -- proves the envelope genuinely persists across Apply calls instead
            // of restarting at 0 every time, the same way IPcmTransform's own cross-block state
            // contract requires.
            var samples = Enumerable.Repeat((int)short.MaxValue, 20).ToArray();

            var singleCallTransform = new CompressorTransform(sampleRate: 1000, thresholdDb: -6, ratio: 4, attackMs: 100, releaseMs: 100);
            var (singleCallOutput, _) = singleCallTransform.Apply(samples.ToArray(), frameCount: 20, channels: 1, sampleRate: 1000, bitsPerSample: 16);

            var splitCallTransform = new CompressorTransform(sampleRate: 1000, thresholdDb: -6, ratio: 4, attackMs: 100, releaseMs: 100);
            var (firstHalfOutput, _) = splitCallTransform.Apply(samples[..10], frameCount: 10, channels: 1, sampleRate: 1000, bitsPerSample: 16);
            var (secondHalfOutput, _) = splitCallTransform.Apply(samples[10..], frameCount: 10, channels: 1, sampleRate: 1000, bitsPerSample: 16);

            firstHalfOutput.Concat(secondHalfOutput).Should().Equal(singleCallOutput);
        }

        [Fact]
        public void Reset_Should_ClearTheEnvelope_BackToSilence()
        {
            // A slow release means the envelope would still read well above threshold immediately
            // after a loud sample if Reset() didn't actually clear it back to 0.
            var transform = new CompressorTransform(sampleRate: 1000, thresholdDb: -6, ratio: 4, attackMs: 0, releaseMs: 1000);
            transform.Apply([(int)short.MaxValue], frameCount: 1, channels: 1, sampleRate: 1000, bitsPerSample: 16);

            transform.Reset();
            var (outBuffer, _) = transform.Apply([100], frameCount: 1, channels: 1, sampleRate: 1000, bitsPerSample: 16);

            outBuffer[0].Should().Be(100, "the envelope should have been cleared back to 0, so this quiet sample is nowhere near threshold");
        }

        [Fact]
        public void Apply_WithZeroFrameCount_Should_ReturnUnchanged()
        {
            var transform = new CompressorTransform(sampleRate: 44100, thresholdDb: -6, ratio: 4, attackMs: 0, releaseMs: 0);
            var buffer = new[] { 1, 2, 3 };

            var (outBuffer, frameCount) = transform.Apply(buffer, frameCount: 0, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            outBuffer.Should().BeSameAs(buffer);
            frameCount.Should().Be(0);
        }

        [Fact]
        public void Apply_WithMismatchedSampleRate_Should_Throw()
        {
            var transform = new CompressorTransform(sampleRate: 44100, thresholdDb: -6, ratio: 4, attackMs: 0, releaseMs: 0);

            var act = () => transform.Apply([1], frameCount: 1, channels: 1, sampleRate: 48000, bitsPerSample: 16);

            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Apply_LargeMakeupGain_Should_Clamp_To_The_Current_Bit_Depths_Native_Range()
        {
            var transform = new CompressorTransform(sampleRate: 44100, thresholdDb: -6, ratio: 4, attackMs: 0, releaseMs: 0, makeupGainDb: 40);
            var buffer = new[] { 1000, -1000 }; // well below threshold (no reduction), but *100 linear gain (+40dB) overflows 16-bit range either way

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 2, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            outBuffer.Should().Equal(short.MaxValue, short.MinValue);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Constructor_WithNonPositiveSampleRate_Should_Throw(int sampleRate)
        {
            var act = () => new CompressorTransform(sampleRate, thresholdDb: -6, ratio: 4, attackMs: 0, releaseMs: 0);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Theory]
        [InlineData(0.1)]
        [InlineData(double.NaN)]
        public void Constructor_WithPositiveOrNaNThreshold_Should_Throw(double thresholdDb)
        {
            var act = () => new CompressorTransform(sampleRate: 44100, thresholdDb, ratio: 4, attackMs: 0, releaseMs: 0);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Theory]
        [InlineData(0.99)]
        [InlineData(double.NaN)]
        public void Constructor_WithRatioBelowOneOrNaN_Should_Throw(double ratio)
        {
            var act = () => new CompressorTransform(sampleRate: 44100, thresholdDb: -6, ratio, attackMs: 0, releaseMs: 0);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(double.NaN)]
        public void Constructor_WithNegativeOrNaNAttack_Should_Throw(double attackMs)
        {
            var act = () => new CompressorTransform(sampleRate: 44100, thresholdDb: -6, ratio: 4, attackMs, releaseMs: 0);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(double.NaN)]
        public void Constructor_WithNegativeOrNaNRelease_Should_Throw(double releaseMs)
        {
            var act = () => new CompressorTransform(sampleRate: 44100, thresholdDb: -6, ratio: 4, attackMs: 0, releaseMs);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        public void Constructor_WithNonFiniteMakeupGain_Should_Throw(double makeupGainDb)
        {
            var act = () => new CompressorTransform(sampleRate: 44100, thresholdDb: -6, ratio: 4, attackMs: 0, releaseMs: 0, makeupGainDb);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }
    }
}
