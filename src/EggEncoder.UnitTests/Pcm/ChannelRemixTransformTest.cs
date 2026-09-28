using EggEncoder.Pcm;
using FluentAssertions;

namespace EggEncoder.UnitTests.Pcm
{
    public class ChannelRemixTransformTest
    {
        [Fact]
        public void Apply_Identity_Should_Return_Same_Buffer_Unchanged()
        {
            var transform = new ChannelRemixTransform(inputChannels: 2, outputChannels: 2);
            var buffer = new[] { 1, 2, 3, 4 };

            var (outBuffer, frameCount) = transform.Apply(buffer, frameCount: 2, channels: 2, sampleRate: 44100, bitsPerSample: 16);

            outBuffer.Should().BeSameAs(buffer);
            frameCount.Should().Be(2);
        }

        [Fact]
        public void Apply_StereoToMono_Should_Average_LeftAndRight()
        {
            var transform = new ChannelRemixTransform(inputChannels: 2, outputChannels: 1);
            var buffer = new[] { 1000, 2000, 3000, 4000 };

            var (outBuffer, frameCount) = transform.Apply(buffer, frameCount: 2, channels: 2, sampleRate: 44100, bitsPerSample: 16);

            frameCount.Should().Be(2);
            outBuffer.Should().Equal(1500, 3500);
        }

        [Fact]
        public void Apply_MonoToStereo_Should_Duplicate_Channel()
        {
            var transform = new ChannelRemixTransform(inputChannels: 1, outputChannels: 2);
            var buffer = new[] { 1000, 2000 };

            var (outBuffer, frameCount) = transform.Apply(buffer, frameCount: 2, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            frameCount.Should().Be(2);
            outBuffer.Should().Equal(1000, 1000, 2000, 2000);
        }

        [Fact]
        public void Apply_FourToTwo_Downmix_Should_Average_Equal_Weight_Groups()
        {
            var transform = new ChannelRemixTransform(inputChannels: 4, outputChannels: 2);
            var buffer = new[] { 0, 100, 200, 300 };

            var (outBuffer, frameCount) = transform.Apply(buffer, frameCount: 1, channels: 4, sampleRate: 44100, bitsPerSample: 16);

            frameCount.Should().Be(1);
            outBuffer.Should().Equal(50, 250);
        }

        [Fact]
        public void Apply_ThreeToTwo_Downmix_Should_Not_DoubleCount_A_Boundary_Channel()
        {
            // Regression test: with inCh/outCh not evenly divisible, independently floor/ceil-ing each
            // group's boundaries let a boundary channel land in two groups at once (e.g. the center
            // channel counted toward both outputs), so its total contribution outweighed L/R's. Boundaries
            // must instead partition every input channel into exactly one group.
            var transform = new ChannelRemixTransform(inputChannels: 3, outputChannels: 2);
            var buffer = new[] { 100, 200, 300 }; // L, C, R

            var (outBuffer, frameCount) = transform.Apply(buffer, frameCount: 1, channels: 3, sampleRate: 44100, bitsPerSample: 16);

            frameCount.Should().Be(1);
            outBuffer.Should().Equal(150, 300); // group0 = avg(L,C); group1 = R alone
        }

        [Fact]
        public void Apply_OneToFour_Upmix_Should_Duplicate_Cyclically()
        {
            var transform = new ChannelRemixTransform(inputChannels: 1, outputChannels: 4);
            var buffer = new[] { 500 };

            var (outBuffer, frameCount) = transform.Apply(buffer, frameCount: 1, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            frameCount.Should().Be(1);
            outBuffer.Should().Equal(500, 500, 500, 500);
        }

        [Theory]
        [InlineData(0, 2)]
        [InlineData(2, 0)]
        [InlineData(-1, 2)]
        public void Constructor_NonPositiveChannels_Should_Throw(int inputChannels, int outputChannels)
        {
            var act = () => new ChannelRemixTransform(inputChannels, outputChannels);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Apply_MismatchedActualChannelCount_Should_Throw()
        {
            var transform = new ChannelRemixTransform(inputChannels: 2, outputChannels: 1);
            var act = () => transform.Apply([1, 2, 3], frameCount: 1, channels: 3, sampleRate: 44100, bitsPerSample: 16);

            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Apply_IdentityPassthrough_MismatchedActualChannelCount_Should_Also_Throw()
        {
            // Identity (in == out, Auto mode) skips the mix matrix, but must still validate the actual
            // channel count -- a mismatched pipeline stage shouldn't silently pass corrupt data through.
            var transform = new ChannelRemixTransform(inputChannels: 2, outputChannels: 2);
            var act = () => transform.Apply([1, 2, 3], frameCount: 1, channels: 3, sampleRate: 44100, bitsPerSample: 16);

            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Constructor_PassThroughMode_With_Mismatched_Channels_Should_Throw()
        {
            var act = () => new ChannelRemixTransform(inputChannels: 2, outputChannels: 4, ChannelRemixMode.PassThrough);

            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void OutputChannels_Should_Reflect_Constructor_Argument()
        {
            var transform = new ChannelRemixTransform(inputChannels: 2, outputChannels: 1);

            transform.OutputChannels.Should().Be(1);
            transform.OutputSampleRate.Should().Be(0);
            transform.OutputBitsPerSample.Should().Be(0);
            transform.CanChangeFrameCount.Should().BeFalse();
        }
    }
}
