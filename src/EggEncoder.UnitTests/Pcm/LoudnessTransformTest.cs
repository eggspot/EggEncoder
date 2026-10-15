using EggEncoder.Pcm;
using FluentAssertions;

namespace EggEncoder.UnitTests.Pcm
{
    public class LoudnessTransformTest
    {
        [Fact]
        public void Apply_WithoutMeasureLoudness_Should_Throw()
        {
            // Unlike PeakNormalizationTransform, this transform has no "self-measure from whatever
            // buffer Apply sees first" fallback -- EBU R128 integrated loudness genuinely needs the
            // whole signal, so Apply must refuse rather than guess from one block.
            var transform = new LoudnessTransform(targetLufs: -14.0);

            var act = () => transform.Apply([100, -200], frameCount: 2, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            act.Should().Throw<InvalidOperationException>();
        }

        [Fact]
        public void Apply_Should_Scale_Samples_To_Reach_The_Target_Loudness_Gain()
        {
            // 10 LU quieter measured than target => +10dB gain (10x linear), the same gain-from-dB
            // math PeakNormalizationTransform/VolumeTransform already use.
            var transform = new LoudnessTransform(targetLufs: -14.0);
            transform.MeasureLoudness(-24.0);

            var (outBuffer, _) = transform.Apply([1000, -2000], frameCount: 2, channels: 1, sampleRate: 44100, bitsPerSample: 32);

            var expectedGain = Math.Pow(10, 10.0 / 20.0);
            outBuffer[0].Should().Be((int)Math.Clamp(1000 * expectedGain, int.MinValue, int.MaxValue));
            outBuffer[1].Should().Be((int)Math.Clamp(-2000 * expectedGain, int.MinValue, int.MaxValue));
        }

        [Fact]
        public void Apply_WhenAlreadyAtTarget_Should_Return_Same_Buffer_Unchanged()
        {
            var transform = new LoudnessTransform(targetLufs: -16.0);
            transform.MeasureLoudness(-16.0);
            var buffer = new[] { 1000, -2000, 3000 };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 3, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            outBuffer.Should().BeSameAs(buffer);
        }

        [Fact]
        public void Apply_LargeGain_Should_Clamp_To_The_Current_Bit_Depths_Native_Range()
        {
            // 40 LU quieter than target is an extreme (but legal) gain request -- must clamp to the
            // 16-bit native range, not wrap or overflow, the same convention every other gain-applying
            // transform in this codebase follows.
            var transform = new LoudnessTransform(targetLufs: -14.0);
            transform.MeasureLoudness(-54.0);

            var (outBuffer, _) = transform.Apply([1000, -1000], frameCount: 2, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            outBuffer.Should().Equal(32767, -32768);
        }

        [Fact]
        public void Apply_MeasuredLufsNull_Should_PassThrough_Unchanged()
        {
            // null means loudness genuinely couldn't be measured (e.g. audio shorter than EBU R128's
            // own 400ms minimum gating block) -- there's no finite gain that correctly answers "what
            // gain reaches the target from an unknown starting point", so this must not guess.
            var transform = new LoudnessTransform(targetLufs: -14.0);
            transform.MeasureLoudness(null);
            var buffer = new[] { 1000, -2000 };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 2, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            outBuffer.Should().BeSameAs(buffer);
        }

        [Fact]
        public void Apply_MeasuredLufsNegativeInfinity_Should_PassThrough_Unchanged()
        {
            // EBU R128's own "entirely below the absolute silence gate" sentinel -- a real
            // measurement, not a "couldn't measure" case, but still one no finite gain can act on
            // (target - (-Infinity) is itself infinite).
            var transform = new LoudnessTransform(targetLufs: -14.0);
            transform.MeasureLoudness(double.NegativeInfinity);
            var buffer = new[] { 0, 0, 0 };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 3, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            outBuffer.Should().BeSameAs(buffer);
        }

        [Fact]
        public void MeasureLoudness_CalledTwice_Should_KeepTheFirstMeasurement()
        {
            // Mirrors PeakNormalizationTransform.MeasurePeak's own "first call wins" convention --
            // a pipeline reused across a Flush() cascade, or a caller that measures once up front,
            // must not have that fixed gain silently reset by a later, incidental call.
            var transform = new LoudnessTransform(targetLufs: -14.0);
            transform.MeasureLoudness(-24.0);
            transform.MeasureLoudness(-4.0);

            var (outBuffer, _) = transform.Apply([1000], frameCount: 1, channels: 1, sampleRate: 44100, bitsPerSample: 32);

            var expectedGain = Math.Pow(10, 10.0 / 20.0); // from the first measurement (-24), not the second (-4)
            outBuffer[0].Should().Be((int)Math.Clamp(1000 * expectedGain, int.MinValue, int.MaxValue));
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        public void Constructor_NonFiniteTargetLufs_Should_Throw(double targetLufs)
        {
            var act = () => new LoudnessTransform(targetLufs);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }
    }
}
