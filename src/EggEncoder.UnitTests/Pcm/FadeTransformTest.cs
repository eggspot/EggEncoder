using EggEncoder.Pcm;
using FluentAssertions;

namespace EggEncoder.UnitTests.Pcm
{
    public class FadeTransformTest
    {
        [Fact]
        public void Apply_NoFadeRequested_Should_Return_Same_Buffer_Unchanged()
        {
            var transform = new FadeTransform(totalFrames: 4);
            var buffer = new[] { 1000, 1000, 1000, 1000 };

            var (outBuffer, frameCount) = transform.Apply(buffer, frameCount: 4, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            outBuffer.Should().BeSameAs(buffer);
            frameCount.Should().Be(4);
        }

        [Fact]
        public void Apply_FadeIn_Linear_Should_Ramp_From_Zero()
        {
            var transform = new FadeTransform(totalFrames: 4, fadeInFrames: 4);
            var buffer = new[] { 1000, 1000, 1000, 1000 };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 4, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            outBuffer.Should().Equal(0, 250, 500, 750);
        }

        [Fact]
        public void Apply_FadeOut_Linear_Should_Ramp_To_Zero()
        {
            var transform = new FadeTransform(totalFrames: 4, fadeOutFrames: 4);
            var buffer = new[] { 1000, 1000, 1000, 1000 };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 4, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            outBuffer.Should().Equal(750, 500, 250, 0);
        }

        [Fact]
        public void Apply_FadeIn_EqualPower_Should_Follow_Sine_Curve()
        {
            var transform = new FadeTransform(totalFrames: 4, fadeInFrames: 4, curve: FadeCurve.EqualPower);
            var buffer = new[] { 1000, 1000, 1000, 1000 };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 4, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            outBuffer[0].Should().Be(0);
            outBuffer[2].Should().Be((int)Math.Round(1000 * Math.Sin(0.5 * Math.PI / 2)));
        }

        [Fact]
        public void Apply_FadeIn_Should_Be_Correct_When_It_Spans_Multiple_Blocks()
        {
            // This is the bug the salvaged implementation had: computing gain from the current block's
            // local frame index (always restarting at 0) instead of the frame's position in the whole
            // retained range. Feeding the fade across two blocks must ramp continuously across them.
            var transform = new FadeTransform(totalFrames: 8, fadeInFrames: 8);

            var (firstBlock, _) = transform.Apply([1000, 1000, 1000, 1000], frameCount: 4, channels: 1, sampleRate: 44100, bitsPerSample: 16);
            var (secondBlock, _) = transform.Apply([1000, 1000, 1000, 1000], frameCount: 4, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            firstBlock.Should().Equal(0, 125, 250, 375);
            secondBlock.Should().Equal(500, 625, 750, 875);
        }

        [Fact]
        public void Apply_MultiChannel_Should_Apply_Same_Gain_To_Every_Channel_In_A_Frame()
        {
            var transform = new FadeTransform(totalFrames: 2, fadeInFrames: 2);
            var buffer = new[] { 1000, 2000, 1000, 2000 }; // 2 frames, stereo

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 2, channels: 2, sampleRate: 44100, bitsPerSample: 16);

            outBuffer.Should().Equal(0, 0, 500, 1000);
        }

        [Fact]
        public void Reset_Should_Restart_Position_From_Zero()
        {
            var transform = new FadeTransform(totalFrames: 4, fadeInFrames: 4);
            var buffer = new[] { 1000, 1000, 1000, 1000 };

            var (firstRun, _) = transform.Apply((int[])buffer.Clone(), frameCount: 4, channels: 1, sampleRate: 44100, bitsPerSample: 16);
            transform.Reset();
            var (secondRun, _) = transform.Apply((int[])buffer.Clone(), frameCount: 4, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            secondRun.Should().Equal(firstRun);
        }

        [Fact]
        public void Constructor_FadeFramesLongerThanTotal_Should_Clamp_Without_Throwing()
        {
            var transform = new FadeTransform(totalFrames: 2, fadeInFrames: 100);
            var buffer = new[] { 1000, 1000 };

            var act = () => transform.Apply(buffer, frameCount: 2, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            act.Should().NotThrow();
        }

        [Fact]
        public void Constructor_NegativeArguments_Should_Throw()
        {
            var act = () => new FadeTransform(totalFrames: -1);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }
    }
}
