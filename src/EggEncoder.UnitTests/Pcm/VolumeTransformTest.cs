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
        public void Apply_LargeGain_Should_Clamp_To_The_Current_Bit_Depths_Native_Range_Not_Int32()
        {
            // A 16-bit sample pushed past its own range by gain must clamp to 16-bit min/max, not just
            // fit-in-int32: otherwise the out-of-range value reaches the sink and gets truncated on
            // write (e.g. WavWriter's 16-bit path takes the low two bytes), producing wraparound
            // distortion instead of a clean clip.
            var transform = new VolumeTransform(10.0);
            var buffer = new[] { 10000, -10000 };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 2, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            outBuffer.Should().Equal(32767, -32768);
        }

        [Fact]
        public void Apply_LargeGain_On_32Bit_Should_Clamp_To_Int32_Range()
        {
            var transform = new VolumeTransform(10_000_000_000.0);
            var buffer = new[] { 1, -1 };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 2, channels: 1, sampleRate: 44100, bitsPerSample: 32);

            outBuffer[0].Should().Be(int.MaxValue);
            outBuffer[1].Should().Be(int.MinValue);
        }

        [Fact]
        public void Constructor_NegativeGain_Should_Throw()
        {
            var act = () => new VolumeTransform(-1.0);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        public void Constructor_NonFiniteGain_Should_Throw(double gain)
        {
            // NaN isn't caught by `gain < 0` alone (NaN < 0 is false under IEEE 754), and
            // +Infinity applied to a silent sample would compute 0 * Infinity == NaN -- both must be
            // rejected at construction rather than silently corrupting output later.
            var act = () => new VolumeTransform(gain);

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
            transform.MeasurePeak([2_000_000], frameCount: 1, channels: 1, bitsPerSample: 32);

            var quietBlock = new[] { 100 };
            var (outBuffer, _) = transform.Apply(quietBlock, frameCount: 1, channels: 1, sampleRate: 44100, bitsPerSample: 32);

            var expectedGain = (double)int.MaxValue / 2_000_000;
            outBuffer[0].Should().Be((int)Math.Clamp(100 * expectedGain, int.MinValue, int.MaxValue));
        }

        [Fact]
        public void PeakNormalization_MeasurePeakFromKnownValue_Should_Fix_Gain_Without_A_Buffer()
        {
            // The correct way to get a true whole-file measurement into a block-by-block pipeline: measure
            // the peak once (e.g. via AudioCutter.MeasurePeakAmplitude) and hand the scalar in directly.
            var transform = new PeakNormalizationTransform(targetDb: 0.0);
            transform.MeasurePeak(2_000_000L, bitsPerSample: 32);

            var (outBuffer, _) = transform.Apply([100], frameCount: 1, channels: 1, sampleRate: 44100, bitsPerSample: 32);

            var expectedGain = (double)int.MaxValue / 2_000_000;
            outBuffer[0].Should().Be((int)Math.Clamp(100 * expectedGain, int.MinValue, int.MaxValue));
        }

        [Fact]
        public void PeakNormalization_MeasurePeakFromKnownValue_Should_Rescale_When_Apply_Sees_A_Different_BitDepth()
        {
            // Peak measured at 16-bit (e.g. via AudioCutter.MeasurePeakAmplitude on the source file), but
            // a BitDepthFormatTransform earlier in the same pipeline has since widened Apply's buffer to
            // 24-bit. Gain must be computed against the peak rescaled to 24-bit, not the raw 16-bit value
            // (which would compute a gain ~256x too large and clip everything to the 24-bit ceiling).
            const long peak16 = 16384L; // half of int16's positive range
            var transform = new PeakNormalizationTransform(targetDb: 0.0);
            transform.MeasurePeak(peak16, bitsPerSample: 16);

            var widenedPeakSample = (int)(peak16 << 8); // what BitDepthFormatTransform(16, 24) would produce for this sample
            var (outBuffer, _) = transform.Apply([widenedPeakSample], frameCount: 1, channels: 1, sampleRate: 44100, bitsPerSample: 24);

            // Recompute using the exact same steps Apply() takes: rescale the 16-bit peak to 24-bit,
            // derive gain from that, apply and clamp to the 24-bit range. Since the sample fed in *is*
            // the (widened) peak, correct rescaling should land it almost exactly at the 24-bit ceiling.
            var rescaledPeak = (long)Math.Round(peak16 * 8388607.0 / 32767);
            var expectedGain = 8388607.0 / rescaledPeak;
            var expected = (int)Math.Clamp(widenedPeakSample * expectedGain, -8388608, 8388607);

            outBuffer[0].Should().Be(expected);
            outBuffer[0].Should().BeGreaterThan(8000000); // sanity: nowhere near the ~256x-too-large-gain bug's blown-out output
        }

        [Fact]
        public void PeakNormalization_MeasurePeakFromKnownValue_Zero_Should_Leave_Gain_At_One()
        {
            var transform = new PeakNormalizationTransform(targetDb: -1.0);
            transform.MeasurePeak(0L, bitsPerSample: 16);

            var (outBuffer, _) = transform.Apply([0, 0], frameCount: 2, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            outBuffer.Should().OnlyContain(sample => sample == 0);
        }

        [Fact]
        public void PeakNormalization_PositiveTargetDb_Should_Throw()
        {
            var act = () => new PeakNormalizationTransform(targetDb: 0.1);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.NegativeInfinity)]
        public void PeakNormalization_NonFiniteTargetDb_Should_Throw(double targetDb)
        {
            // NaN isn't caught by `targetDb > 0` alone (NaN > 0 is false under IEEE 754), and would
            // otherwise flow into Math.Pow(10, NaN / 20.0), producing a NaN _targetLin that silently
            // corrupts every gain this transform ever computes.
            var act = () => new PeakNormalizationTransform(targetDb);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }
    }
}
