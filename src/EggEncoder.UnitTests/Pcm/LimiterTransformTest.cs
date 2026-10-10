using EggEncoder.Pcm;
using FluentAssertions;

namespace EggEncoder.UnitTests.Pcm
{
    public class LimiterTransformTest
    {
        [Fact]
        public void Apply_BelowCeiling_Should_Leave_Samples_Unchanged()
        {
            var transform = new LimiterTransform(sampleRate: 44100, ceilingDb: -1, releaseMs: 0, attackMs: 0);
            var buffer = new[] { 100 };

            var (outBuffer, frameCount) = transform.Apply(buffer, frameCount: 1, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            frameCount.Should().Be(1);
            outBuffer.Should().Equal(100);
        }

        [Fact]
        public void Apply_AboveCeiling_WithInstantAttackAndRelease_Should_ClampExactlyToTheCeiling()
        {
            var transform = new LimiterTransform(sampleRate: 44100, ceilingDb: -6, releaseMs: 0, attackMs: 0);
            var buffer = new[] { (int)short.MaxValue };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 1, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            var expectedCeiling = Math.Pow(10.0, -6 / 20.0) * short.MaxValue;
            outBuffer[0].Should().Be((int)expectedCeiling);
        }

        [Fact]
        public void Apply_NegativePeak_AboveCeiling_Should_ClampSymmetrically()
        {
            var transform = new LimiterTransform(sampleRate: 44100, ceilingDb: -6, releaseMs: 0, attackMs: 0);
            var buffer = new[] { (int)short.MinValue };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 1, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            var expectedCeiling = Math.Pow(10.0, -6 / 20.0) * short.MaxValue;
            outBuffer[0].Should().Be(-(int)expectedCeiling);
        }

        [Fact]
        public void Apply_ZeroDbCeiling_Should_AllowFullScale_ButNeverExceedIt()
        {
            var transform = new LimiterTransform(sampleRate: 44100, ceilingDb: 0, releaseMs: 0, attackMs: 0);
            var buffer = new[] { (int)short.MaxValue };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 1, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            outBuffer[0].Should().Be(short.MaxValue);
        }

        [Fact]
        public void Apply_TrueSilence_Should_RemainSilent_WithoutProducingNaN()
        {
            var transform = new LimiterTransform(sampleRate: 44100, ceilingDb: -6, releaseMs: 0, attackMs: 0);
            var buffer = new[] { 0 };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 1, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            outBuffer[0].Should().Be(0);
        }

        [Fact]
        public void Apply_StereoLinkedEnvelope_Should_ReduceBothChannels_WhenOnlyOneChannelExceedsCeiling()
        {
            var transform = new LimiterTransform(sampleRate: 44100, ceilingDb: -6, releaseMs: 0, attackMs: 0);
            var buffer = new[] { (int)short.MaxValue, 1000 }; // channel 0 full-scale, channel 1 quiet

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 1, channels: 2, sampleRate: 44100, bitsPerSample: 16);

            var expectedCeiling = Math.Pow(10.0, -6 / 20.0) * short.MaxValue;
            var expectedGain = expectedCeiling / short.MaxValue;
            outBuffer[0].Should().Be((int)expectedCeiling);
            ((double)outBuffer[1]).Should().BeApproximately(1000 * expectedGain, 1.0, "the loud channel's gain reduction is linked across both channels");
        }

        [Fact]
        public void Apply_WithNonZeroAttack_FirstSample_CanStillExceedTheSmoothedEnvelopeGain_ButNeverTheHardCeiling()
        {
            // With a slow attack, the envelope barely moves on the very first sample, so the
            // envelope-derived gain alone would barely reduce anything -- yet the final per-sample
            // hard clip must still bring the output down to exactly the ceiling. This is the
            // transform's whole reason to exist over "CompressorTransform with a high ratio".
            var transform = new LimiterTransform(sampleRate: 1000, ceilingDb: -6, releaseMs: 100, attackMs: 1000);
            var buffer = new[] { (int)short.MaxValue };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 1, channels: 1, sampleRate: 1000, bitsPerSample: 16);

            var expectedCeiling = (int)(Math.Pow(10.0, -6 / 20.0) * short.MaxValue);
            outBuffer[0].Should().Be(expectedCeiling);
        }

        [Fact]
        public void Apply_WithNonZeroRelease_Should_GraduallyRecover_AfterATransient()
        {
            var transform = new LimiterTransform(sampleRate: 1000, ceilingDb: -6, releaseMs: 100, attackMs: 0);
            var buffer = new int[500];
            buffer[0] = short.MaxValue;
            for (var i = 1; i < buffer.Length; i++) buffer[i] = 100;

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 500, channels: 1, sampleRate: 1000, bitsPerSample: 16);

            outBuffer[1].Should().BeLessThan(100, "the envelope is still elevated from the transient, so even a quiet sample right after it is still being reduced");
            outBuffer[^1].Should().Be(100, "the envelope should have fully released by the end of the buffer, well after 100ms (100 samples at 1000Hz)");
        }

        [Fact]
        public void Apply_SplitAcrossMultipleCalls_Should_Produce_The_Same_Result_As_OneCall()
        {
            var samples = new[] { (int)short.MaxValue }.Concat(Enumerable.Repeat(1000, 19)).ToArray();

            var singleCallTransform = new LimiterTransform(sampleRate: 1000, ceilingDb: -6, releaseMs: 100, attackMs: 0);
            var (singleCallOutput, _) = singleCallTransform.Apply(samples.ToArray(), frameCount: 20, channels: 1, sampleRate: 1000, bitsPerSample: 16);

            var splitCallTransform = new LimiterTransform(sampleRate: 1000, ceilingDb: -6, releaseMs: 100, attackMs: 0);
            var (firstHalfOutput, _) = splitCallTransform.Apply(samples[..10], frameCount: 10, channels: 1, sampleRate: 1000, bitsPerSample: 16);
            var (secondHalfOutput, _) = splitCallTransform.Apply(samples[10..], frameCount: 10, channels: 1, sampleRate: 1000, bitsPerSample: 16);

            firstHalfOutput.Concat(secondHalfOutput).Should().Equal(singleCallOutput);
        }

        [Fact]
        public void Reset_Should_ClearTheEnvelope_BackToSilence()
        {
            var transform = new LimiterTransform(sampleRate: 1000, ceilingDb: -6, releaseMs: 1000, attackMs: 0);
            transform.Apply([(int)short.MaxValue], frameCount: 1, channels: 1, sampleRate: 1000, bitsPerSample: 16);

            transform.Reset();
            var (outBuffer, _) = transform.Apply([100], frameCount: 1, channels: 1, sampleRate: 1000, bitsPerSample: 16);

            outBuffer[0].Should().Be(100, "the envelope should have been cleared back to 0, so this quiet sample is below the ceiling and left untouched, not still reduced from before Reset()");
        }

        [Fact]
        public void Apply_WithZeroFrameCount_Should_ReturnUnchanged()
        {
            var transform = new LimiterTransform(sampleRate: 44100, ceilingDb: -1, releaseMs: 0, attackMs: 0);
            var buffer = new[] { 1, 2, 3 };

            var (outBuffer, frameCount) = transform.Apply(buffer, frameCount: 0, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            outBuffer.Should().BeSameAs(buffer);
            frameCount.Should().Be(0);
        }

        [Fact]
        public void Apply_WithMismatchedSampleRate_Should_Throw()
        {
            var transform = new LimiterTransform(sampleRate: 44100, ceilingDb: -1, releaseMs: 0, attackMs: 0);

            var act = () => transform.Apply([1], frameCount: 1, channels: 1, sampleRate: 48000, bitsPerSample: 16);

            act.Should().Throw<ArgumentException>();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Constructor_WithNonPositiveSampleRate_Should_Throw(int sampleRate)
        {
            var act = () => new LimiterTransform(sampleRate, ceilingDb: -1, releaseMs: 0, attackMs: 0);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Theory]
        [InlineData(0.1)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        public void Constructor_WithPositiveOrNonFiniteCeiling_Should_Throw(double ceilingDb)
        {
            var act = () => new LimiterTransform(sampleRate: 44100, ceilingDb, releaseMs: 0, attackMs: 0);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(double.NaN)]
        public void Constructor_WithNegativeOrNaNAttack_Should_Throw(double attackMs)
        {
            var act = () => new LimiterTransform(sampleRate: 44100, ceilingDb: -1, releaseMs: 0, attackMs);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(double.NaN)]
        public void Constructor_WithNegativeOrNaNRelease_Should_Throw(double releaseMs)
        {
            var act = () => new LimiterTransform(sampleRate: 44100, ceilingDb: -1, releaseMs, attackMs: 0);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }
    }
}
