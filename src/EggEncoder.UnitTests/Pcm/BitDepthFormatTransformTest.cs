using EggEncoder.Pcm;
using FluentAssertions;

namespace EggEncoder.UnitTests.Pcm
{
    public class BitDepthFormatTransformTest
    {
        [Fact]
        public void Apply_SameBitDepth_Should_Return_Same_Buffer_Unchanged()
        {
            var transform = new BitDepthFormatTransform(fromBits: 16, toBits: 16);
            var buffer = new[] { 100, -100 };

            var (outBuffer, frameCount) = transform.Apply(buffer, frameCount: 2, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            outBuffer.Should().BeSameAs(buffer);
            frameCount.Should().Be(2);
        }

        [Fact]
        public void Apply_Widening_16To24_Should_LeftShift_Exactly()
        {
            var transform = new BitDepthFormatTransform(fromBits: 16, toBits: 24);
            var buffer = new[] { 100, -100, 0, short.MaxValue, short.MinValue };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 5, channels: 1, sampleRate: 44100, bitsPerSample: 16);

            outBuffer.Should().Equal(100 << 8, -100 << 8, 0, short.MaxValue << 8, short.MinValue << 8);
        }

        [Fact]
        public void Apply_Narrowing_24To16_Should_Invert_Widening_For_Exact_Values()
        {
            var widen = new BitDepthFormatTransform(fromBits: 16, toBits: 24);
            var narrow = new BitDepthFormatTransform(fromBits: 24, toBits: 16);
            var original = new[] { 100, -100, 12345, -12345 };

            var (widened, frameCount) = widen.Apply((int[])original.Clone(), frameCount: 4, channels: 1, sampleRate: 44100, bitsPerSample: 16);
            var (roundTripped, _) = narrow.Apply(widened, frameCount, channels: 1, sampleRate: 44100, bitsPerSample: 24);

            // A pure left-shift by 8 (widen) followed by rounded division by 256 (narrow) recovers the
            // exact original value whenever the original is itself a multiple of 1 (always, for ints) and
            // small enough that no clamping occurs -- verified here rather than just documented.
            roundTripped.Should().Equal(original);
        }

        [Fact]
        public void Apply_Narrowing_Should_Round_To_Nearest()
        {
            var transform = new BitDepthFormatTransform(fromBits: 24, toBits: 16);

            // 200 / 256 = 0.78125 -> rounds up to 1; 100 / 256 = 0.390625 -> rounds down to 0.
            var (outBuffer, _) = transform.Apply([200, 100], frameCount: 2, channels: 1, sampleRate: 44100, bitsPerSample: 24);

            outBuffer.Should().Equal(1, 0);
        }

        [Fact]
        public void Apply_Narrowing_Should_Clamp_To_Destination_Range()
        {
            var transform = new BitDepthFormatTransform(fromBits: 32, toBits: 8);

            var (outBuffer, _) = transform.Apply([int.MaxValue, int.MinValue], frameCount: 2, channels: 1, sampleRate: 44100, bitsPerSample: 32);

            outBuffer.Should().Equal(127, -128);
        }

        [Fact]
        public void Apply_MismatchedActualBitsPerSample_Should_Throw()
        {
            var transform = new BitDepthFormatTransform(fromBits: 16, toBits: 24);
            var act = () => transform.Apply([1, 2], frameCount: 2, channels: 1, sampleRate: 44100, bitsPerSample: 8);

            act.Should().Throw<ArgumentException>();
        }

        [Theory]
        [InlineData(12)]
        [InlineData(0)]
        [InlineData(64)]
        public void Constructor_InvalidBitDepth_Should_Throw(int invalidBits)
        {
            var act = () => new BitDepthFormatTransform(fromBits: invalidBits, toBits: 16);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void OutputBitsPerSample_Should_Reflect_ToBits()
        {
            var transform = new BitDepthFormatTransform(fromBits: 16, toBits: 24);

            transform.OutputBitsPerSample.Should().Be(24);
            transform.OutputChannels.Should().Be(0);
            transform.OutputSampleRate.Should().Be(0);
        }
    }
}
