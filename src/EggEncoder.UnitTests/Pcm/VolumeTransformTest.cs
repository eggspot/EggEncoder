using EggEncoder.Pcm;
using FluentAssertions;

namespace EggEncoder.UnitTests.Pcm
{
    public class VolumeTransformTest
    {
        [Fact]
        public void Apply_GainOfOne_Should_Return_Same_Buffer_Unchanged()
        {
            var transform = new VolumeTransform(1.0);
            var buffer = new[] { 100, -200, 300 };

            var (outBuffer, frameCount) = transform.Apply(buffer, frameCount: 3, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            outBuffer.Should().BeSameAs(buffer);
            frameCount.Should().Be(3);
        }

        [Fact]
        public void Apply_GainOfZero_Should_Silence_Buffer()
        {
            var transform = new VolumeTransform(0.0);
            var buffer = new[] { 100, -200, 300 };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 3, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            outBuffer.Should().OnlyContain(sample => sample == 0);
        }

        [Fact]
        public void Apply_GainOfHalf_Should_Halve_Each_Sample()
        {
            var transform = new VolumeTransform(0.5);
            var buffer = new[] { 100, -200, 300, -1 };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 4, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            outBuffer.Should().Equal(50, -100, 150, 0);
        }

        [Fact]
        public void Apply_LargeGain_Should_Clamp_To_Int_Range()
        {
            var transform = new VolumeTransform(10_000_000_000.0);
            var buffer = new[] { 1, -1 };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 2, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            outBuffer[0].Should().Be(int.MaxValue);
            outBuffer[1].Should().Be(int.MinValue);
        }

        [Fact]
        public void Constructor_NegativeGain_Should_Throw()
        {
            var act = () => new VolumeTransform(-1.0);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void PeakNormalization_Should_Scale_Peak_To_Target_dBFS()
        {
            var transform = new PeakNormalizationTransform(targetDb: 0.0);
            var buffer = new[] { 1_000_000, -500_000, 250_000 };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 3, channels: 1, sampleRate: 44100, bitsPerSample: 32);

            // targetDb == 0 => target linear peak == 1.0 == int.MaxValue; the loudest sample (1,000,000)
            // should scale up so its magnitude is at (or within float rounding of) int.MaxValue.
            var expectedGain = (double)int.MaxValue / 1_000_000;
            outBuffer[0].Should().Be((int)Math.Clamp(1_000_000 * expectedGain, int.MinValue, int.MaxValue));
            outBuffer[1].Should().Be((int)Math.Clamp(-500_000 * expectedGain, int.MinValue, int.MaxValue));
        }

        [Fact]
        public void PeakNormalization_SilentBuffer_Should_Remain_Silent()
        {
            var transform = new PeakNormalizationTransform(targetDb: -1.0);
            var buffer = new[] { 0, 0, 0 };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 3, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            outBuffer.Should().OnlyContain(sample => sample == 0);
        }

        [Fact]
        public void PeakNormalization_MeasurePeak_Should_Fix_Gain_For_Later_Apply_Calls()
        {
            // Two-pass usage: measure peak across (potentially all of) the stream first, then apply the
            // resulting fixed gain to blocks as they're re-decoded, regardless of each block's own peak.
            var transform = new PeakNormalizationTransform(targetDb: 0.0);
            transform.MeasurePeak([2_000_000], frameCount: 1, channels: 1);

            var quietBlock = new[] { 100 };
            var (outBuffer, _) = transform.Apply(quietBlock, frameCount: 1, channels: 1, sampleRate: 44100, bitsPerSample: 32);

            var expectedGain = (double)int.MaxValue / 2_000_000;
            outBuffer[0].Should().Be((int)Math.Clamp(100 * expectedGain, int.MinValue, int.MaxValue));
        }

        [Fact]
        public void PeakNormalization_PositiveTargetDb_Should_Throw()
        {
            var act = () => new PeakNormalizationTransform(targetDb: 0.1);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }
    }
}
