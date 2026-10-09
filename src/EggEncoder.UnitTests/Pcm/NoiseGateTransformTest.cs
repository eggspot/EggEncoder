using EggEncoder.Pcm;
using FluentAssertions;

namespace EggEncoder.UnitTests.Pcm
{
    public class NoiseGateTransformTest
    {
        [Fact]
        public void Apply_AboveThreshold_Should_Leave_Samples_Unchanged()
        {
            var transform = new NoiseGateTransform(sampleRate: 44100, thresholdDb: -6, ratio: 4, attackMs: 0, releaseMs: 0);
            var buffer = new[] { (int)short.MaxValue };

            var (outBuffer, frameCount) = transform.Apply(buffer, frameCount: 1, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            frameCount.Should().Be(1);
            outBuffer.Should().Equal(short.MaxValue);
        }

        [Fact]
        public void Apply_BelowThreshold_WithInstantAttack_Should_Apply_The_Computed_Reduction()
        {
            const double thresholdDb = -6;
            const double ratio = 4;
            var transform = new NoiseGateTransform(sampleRate: 44100, thresholdDb, ratio, attackMs: 0, releaseMs: 0);
            var buffer = new[] { 100 }; // well below -6dBFS of 16-bit full scale (~16422)

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 1, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            var envelopeDb = 20.0 * Math.Log10(100.0 / short.MaxValue);
            var reductionDb = (thresholdDb - envelopeDb) * (1.0 - (1.0 / ratio));
            var expectedGain = Math.Pow(10.0, -reductionDb / 20.0);
            var expected = (int)Math.Clamp(100 * expectedGain, short.MinValue, short.MaxValue);
            outBuffer[0].Should().Be(expected);
        }

        [Fact]
        public void Apply_RatioOfOne_Should_Never_Reduce_EvenBelowThreshold()
        {
            var transform = new NoiseGateTransform(sampleRate: 44100, thresholdDb: -6, ratio: 1.0, attackMs: 0, releaseMs: 0);
            var buffer = new[] { 100 };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 1, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            outBuffer[0].Should().Be(100, "a ratio of 1.0 means 1-1/ratio == 0, so reductionDb is always 0 regardless of level");
        }

        [Fact]
        public void Apply_RatioOfOne_WithTrueSilence_Should_Not_Produce_NaN()
        {
            // The combination this type's own doc comment warns about: envelopeDb == -Infinity
            // (true digital silence) together with ratio == 1.0 would compute Infinity * 0 == NaN
            // under the general formula -- the explicit ratio == 1.0 short-circuit must come first.
            var transform = new NoiseGateTransform(sampleRate: 44100, thresholdDb: -6, ratio: 1.0, attackMs: 0, releaseMs: 0);
            var buffer = new[] { 0 };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 1, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            outBuffer[0].Should().Be(0);
        }

        [Fact]
        public void Apply_TrueSilence_WithRealRatio_Should_FullyGate_WithoutProducing_NaN()
        {
            var transform = new NoiseGateTransform(sampleRate: 44100, thresholdDb: -6, ratio: 4, attackMs: 0, releaseMs: 0);
            var buffer = new[] { 0 };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 1, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            outBuffer[0].Should().Be(0, "silence gated at any finite ratio is still silence, not NaN");
        }

        [Fact]
        public void Apply_VeryHighRatio_Should_Approach_But_Match_The_Computed_NearFullReduction()
        {
            const double thresholdDb = -6;
            const double ratio = 1000;
            var transform = new NoiseGateTransform(sampleRate: 44100, thresholdDb, ratio, attackMs: 0, releaseMs: 0);
            var buffer = new[] { 100 };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 1, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            var envelopeDb = 20.0 * Math.Log10(100.0 / short.MaxValue);
            var reductionDb = (thresholdDb - envelopeDb) * (1.0 - (1.0 / ratio));
            var expectedGain = Math.Pow(10.0, -reductionDb / 20.0);
            var expected = (int)Math.Clamp(100 * expectedGain, short.MinValue, short.MaxValue);
            outBuffer[0].Should().Be(expected);
        }

        [Fact]
        public void Apply_StereoLinkedEnvelope_Should_NotGate_WhenEitherChannelIsAboveThreshold()
        {
            var transform = new NoiseGateTransform(sampleRate: 44100, thresholdDb: -6, ratio: 4, attackMs: 0, releaseMs: 0);
            var buffer = new[] { short.MaxValue, 0 }; // channel 0 full-scale, channel 1 silent

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 1, channels: 2, sampleRate: 44100, bitsPerSample: 16);

            outBuffer[0].Should().Be(short.MaxValue, "the loud channel keeps the gate open for both channels");
            outBuffer[1].Should().Be(0, "a silent sample times any finite gain is still silent");
        }

        [Fact]
        public void Apply_WithNonZeroAttack_FirstSample_Should_Remain_AtFullReduction()
        {
            // After just one step, the envelope has only moved a tiny fraction of the way up from
            // silence -- nowhere near reaching the -6dBFS threshold yet, so the gate is still fully
            // closed on the very first sample despite the input being full-scale.
            var transform = new NoiseGateTransform(sampleRate: 1000, thresholdDb: -6, ratio: 4, attackMs: 100, releaseMs: 100);
            var buffer = new[] { (int)short.MaxValue };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 1, channels: 1, sampleRate: 1000, bitsPerSample: 16);

            outBuffer[0].Should().BeLessThan(short.MaxValue);
        }

        [Fact]
        public void Apply_WithNonZeroAttack_Should_EventuallyOpen_AfterManySamples()
        {
            var transform = new NoiseGateTransform(sampleRate: 1000, thresholdDb: -6, ratio: 4, attackMs: 100, releaseMs: 100);
            var buffer = Enumerable.Repeat((int)short.MaxValue, 500).ToArray();

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 500, channels: 1, sampleRate: 1000, bitsPerSample: 16);

            outBuffer[0].Should().BeLessThan(short.MaxValue, "the gate should still be closing on the very first sample");
            outBuffer[^1].Should().Be(short.MaxValue, "the envelope should have caught up and opened the gate well before 500 samples (100ms attack at 1000Hz = 100 samples)");
        }

        [Fact]
        public void Apply_SplitAcrossMultipleCalls_Should_Produce_The_Same_Result_As_OneCall()
        {
            var samples = Enumerable.Repeat((int)short.MaxValue, 20).ToArray();

            var singleCallTransform = new NoiseGateTransform(sampleRate: 1000, thresholdDb: -6, ratio: 4, attackMs: 100, releaseMs: 100);
            var (singleCallOutput, _) = singleCallTransform.Apply(samples.ToArray(), frameCount: 20, channels: 1, sampleRate: 1000, bitsPerSample: 16);

            var splitCallTransform = new NoiseGateTransform(sampleRate: 1000, thresholdDb: -6, ratio: 4, attackMs: 100, releaseMs: 100);
            var (firstHalfOutput, _) = splitCallTransform.Apply(samples[..10], frameCount: 10, channels: 1, sampleRate: 1000, bitsPerSample: 16);
            var (secondHalfOutput, _) = splitCallTransform.Apply(samples[10..], frameCount: 10, channels: 1, sampleRate: 1000, bitsPerSample: 16);

            firstHalfOutput.Concat(secondHalfOutput).Should().Equal(singleCallOutput);
        }

        [Fact]
        public void Reset_Should_ClearTheEnvelope_BackToSilence()
        {
            // A slow release means the envelope would still read well above silence immediately
            // after a loud sample if Reset() didn't actually clear it back to 0 -- i.e. the gate
            // would stay open instead of fully closing on the very next (quiet) sample.
            var transform = new NoiseGateTransform(sampleRate: 1000, thresholdDb: -6, ratio: 4, attackMs: 0, releaseMs: 1000);
            transform.Apply([(int)short.MaxValue], frameCount: 1, channels: 1, sampleRate: 1000, bitsPerSample: 16);

            transform.Reset();
            var (outBuffer, _) = transform.Apply([0], frameCount: 1, channels: 1, sampleRate: 1000, bitsPerSample: 16);

            outBuffer[0].Should().Be(0, "the envelope should have been cleared back to 0, so this silent sample is fully gated, not left open from before Reset()");
        }

        [Fact]
        public void Apply_WithZeroFrameCount_Should_ReturnUnchanged()
        {
            var transform = new NoiseGateTransform(sampleRate: 44100, thresholdDb: -6, ratio: 4, attackMs: 0, releaseMs: 0);
            var buffer = new[] { 1, 2, 3 };

            var (outBuffer, frameCount) = transform.Apply(buffer, frameCount: 0, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            outBuffer.Should().BeSameAs(buffer);
            frameCount.Should().Be(0);
        }

        [Fact]
        public void Apply_WithMismatchedSampleRate_Should_Throw()
        {
            var transform = new NoiseGateTransform(sampleRate: 44100, thresholdDb: -6, ratio: 4, attackMs: 0, releaseMs: 0);

            var act = () => transform.Apply([1], frameCount: 1, channels: 1, sampleRate: 48000, bitsPerSample: 16);

            act.Should().Throw<ArgumentException>();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Constructor_WithNonPositiveSampleRate_Should_Throw(int sampleRate)
        {
            var act = () => new NoiseGateTransform(sampleRate, thresholdDb: -6, ratio: 4, attackMs: 0, releaseMs: 0);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Theory]
        [InlineData(0.1)]
        [InlineData(double.NaN)]
        public void Constructor_WithPositiveOrNaNThreshold_Should_Throw(double thresholdDb)
        {
            var act = () => new NoiseGateTransform(sampleRate: 44100, thresholdDb, ratio: 4, attackMs: 0, releaseMs: 0);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Theory]
        [InlineData(0.99)]
        [InlineData(double.NaN)]
        public void Constructor_WithRatioBelowOneOrNaN_Should_Throw(double ratio)
        {
            var act = () => new NoiseGateTransform(sampleRate: 44100, thresholdDb: -6, ratio, attackMs: 0, releaseMs: 0);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(double.NaN)]
        public void Constructor_WithNegativeOrNaNAttack_Should_Throw(double attackMs)
        {
            var act = () => new NoiseGateTransform(sampleRate: 44100, thresholdDb: -6, ratio: 4, attackMs, releaseMs: 0);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(double.NaN)]
        public void Constructor_WithNegativeOrNaNRelease_Should_Throw(double releaseMs)
        {
            var act = () => new NoiseGateTransform(sampleRate: 44100, thresholdDb: -6, ratio: 4, attackMs: 0, releaseMs);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }
    }
}
