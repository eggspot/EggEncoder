using EggEncoder.Pcm;
using FluentAssertions;

namespace EggEncoder.UnitTests.Pcm
{
    public class PanTransformTest
    {
        [Fact]
        public void Apply_PanZero_Linear_Should_Return_Same_Buffer_Unchanged()
        {
            var transform = new PanTransform(0.0);
            var buffer = new[] { 1000, 2000 };

            var (outBuffer, frameCount) = transform.Apply(buffer, frameCount: 1, channels: 2, sampleRate: 44100, bitsPerSample: 16);

            outBuffer.Should().BeSameAs(buffer);
            frameCount.Should().Be(1);
        }

        [Fact]
        public void Apply_PanFullLeft_Linear_Should_Silence_RightChannel_And_Keep_Left_At_Unity()
        {
            var transform = new PanTransform(-1.0);
            var buffer = new[] { 1000, 2000 };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 1, channels: 2, sampleRate: 44100, bitsPerSample: 16);

            outBuffer.Should().Equal(1000, 0);
        }

        [Fact]
        public void Apply_PanFullRight_Linear_Should_Silence_LeftChannel_And_Keep_Right_At_Unity()
        {
            var transform = new PanTransform(1.0);
            var buffer = new[] { 1000, 2000 };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 1, channels: 2, sampleRate: 44100, bitsPerSample: 16);

            outBuffer.Should().Equal(0, 2000);
        }

        [Fact]
        public void Apply_PanFractionalPositive_Linear_Should_Attenuate_OnlyTheLeftChannel()
        {
            // pan=0.5 -> leftGain = 1.0 - 0.5 = 0.5, rightGain unchanged at 1.0.
            var transform = new PanTransform(0.5);
            var buffer = new[] { 1000, 2000 };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 1, channels: 2, sampleRate: 44100, bitsPerSample: 16);

            outBuffer.Should().Equal(500, 2000);
        }

        [Fact]
        public void Apply_PanFractionalNegative_Linear_Should_Attenuate_OnlyTheRightChannel()
        {
            // pan=-0.5 -> rightGain = 1.0 + (-0.5) = 0.5, leftGain unchanged at 1.0.
            var transform = new PanTransform(-0.5);
            var buffer = new[] { 1000, 2000 };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 1, channels: 2, sampleRate: 44100, bitsPerSample: 16);

            outBuffer.Should().Equal(1000, 1000);
        }

        [Fact]
        public void Apply_PanZero_EqualPower_Should_Attenuate_BothChannels_By_CosPiOverFour()
        {
            // Unlike Linear, EqualPower's center is NOT a no-op -- both channels are attenuated by
            // cos(pi/4) == sin(pi/4) (~-3dB) so that left^2 + right^2 == 1 even at center.
            var transform = new PanTransform(0.0, PanLaw.EqualPower);
            var buffer = new[] { 1000, 1000 };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 1, channels: 2, sampleRate: 44100, bitsPerSample: 16);

            var expectedGain = Math.Cos(Math.PI / 4.0);
            outBuffer.Should().Equal((int)(1000 * expectedGain), (int)(1000 * expectedGain));
        }

        [Fact]
        public void Apply_PanFullLeft_EqualPower_Should_Silence_RightChannel()
        {
            var transform = new PanTransform(-1.0, PanLaw.EqualPower);
            var buffer = new[] { 1000, 2000 };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 1, channels: 2, sampleRate: 44100, bitsPerSample: 16);

            outBuffer.Should().Equal(1000, 0);
        }

        [Fact]
        public void Apply_PanFullRight_EqualPower_Should_Silence_LeftChannel()
        {
            var transform = new PanTransform(1.0, PanLaw.EqualPower);
            var buffer = new[] { 1000, 2000 };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 1, channels: 2, sampleRate: 44100, bitsPerSample: 16);

            outBuffer.Should().Equal(0, 2000);
        }

        [Fact]
        public void Apply_PanFractional_EqualPower_Should_Match_The_Trig_Formula()
        {
            var transform = new PanTransform(0.5, PanLaw.EqualPower);
            var buffer = new[] { 1000, 1000 };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 1, channels: 2, sampleRate: 44100, bitsPerSample: 16);

            var theta = (0.5 + 1.0) * Math.PI / 4.0;
            outBuffer.Should().Equal((int)(1000 * Math.Cos(theta)), (int)(1000 * Math.Sin(theta)));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        public void Apply_WithNonStereoChannelCount_Should_Throw(int channels)
        {
            var transform = new PanTransform(0.5);
            var buffer = new int[channels];

            var act = () => transform.Apply(buffer, frameCount: 1, channels, sampleRate: 44100, bitsPerSample: 16);

            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Apply_WithMonoChannelCount_And_PanZero_Should_StillThrow()
        {
            // Validated before the no-op fast path (see PanTransform.Apply's own doc comment): a
            // pan of exactly 0.0 under Linear is otherwise a true identity transform for any channel
            // count, but silently passing a mono buffer through would hide a real misconfiguration.
            var transform = new PanTransform(0.0);
            var buffer = new[] { 1000 };

            var act = () => transform.Apply(buffer, frameCount: 1, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Apply_LargeMagnitude_Should_Clamp_To_The_Current_Bit_Depths_Native_Range()
        {
            // Mirrors VolumeTransformTest's own analogous clamp test: a boosted channel (EqualPower's
            // own center never boosts, but a custom law extension could; more importantly, the
            // extreme input itself could already sit at the bit depth's own boundary) must clamp to
            // the native range, not wrap.
            var transform = new PanTransform(1.0); // leftGain = 0 (silenced), rightGain = 1 (unchanged)
            var buffer = new[] { (int)short.MinValue, (int)short.MaxValue };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 1, channels: 2, sampleRate: 44100, bitsPerSample: 16);

            outBuffer.Should().Equal(0, short.MaxValue);
        }

        [Fact]
        public void Apply_MultipleFrames_Should_Apply_The_Same_Gain_To_Every_Frame()
        {
            var transform = new PanTransform(-1.0); // right silenced, left unchanged
            var buffer = new[] { 100, 200, 300, 400 };

            var (outBuffer, frameCount) = transform.Apply(buffer, frameCount: 2, channels: 2, sampleRate: 44100, bitsPerSample: 16);

            frameCount.Should().Be(2);
            outBuffer.Should().Equal(100, 0, 300, 0);
        }

        [Theory]
        [InlineData(-1.1)]
        [InlineData(1.1)]
        public void Constructor_PanOutOfRange_Should_Throw(double pan)
        {
            var act = () => new PanTransform(pan);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Constructor_UnsupportedPanLaw_Should_Throw()
        {
            var act = () => new PanTransform(0.0, (PanLaw)99);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Pan_And_Law_Properties_Should_Reflect_Constructor_Arguments()
        {
            var transform = new PanTransform(0.25, PanLaw.EqualPower);

            transform.Pan.Should().Be(0.25);
            transform.Law.Should().Be(PanLaw.EqualPower);
        }
    }
}
