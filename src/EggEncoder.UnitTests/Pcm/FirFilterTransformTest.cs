using EggEncoder.Pcm;
using FluentAssertions;

namespace EggEncoder.UnitTests.Pcm
{
    public class FirFilterTransformTest
    {
        private const int SampleRate = 44100;

        // -------- Construction / validation --------

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Constructor_ChannelsNonPositive_Should_Throw(int channels)
        {
            var act = () => new FirFilterTransform(channels, [1.0]);

            act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("channels");
        }

        [Fact]
        public void Constructor_NullTaps_Should_Throw()
        {
            var act = () => new FirFilterTransform(channels: 1, taps: null!);

            act.Should().Throw<ArgumentNullException>().And.ParamName.Should().Be("taps");
        }

        [Fact]
        public void Constructor_EmptyTaps_Should_Throw()
        {
            var act = () => new FirFilterTransform(channels: 1, taps: []);

            act.Should().Throw<ArgumentException>().And.ParamName.Should().Be("taps");
        }

        [Fact]
        public void Constructor_NaNTap_Should_Throw()
        {
            var act = () => new FirFilterTransform(channels: 1, [0.5, double.NaN, 0.5]);

            act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("taps");
        }

        [Theory]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        public void Constructor_InfiniteTap_Should_Throw(double badTap)
        {
            var act = () => new FirFilterTransform(channels: 1, [0.5, badTap, 0.5]);

            act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("taps");
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(8)]
        public void Constructor_ValidTaps_Should_Not_Throw(int tapCount)
        {
            var taps = new double[tapCount];
            Array.Fill(taps, 1.0 / tapCount);

            var act = () => new FirFilterTransform(channels: 2, taps);

            act.Should().NotThrow();
        }

        [Fact]
        public void Constructor_Should_Defensively_Copy_Taps()
        {
            // Mutating the caller's array after construction must not affect the transform -- otherwise
            // a caller reusing/clearing their own taps buffer would silently corrupt an already-built
            // transform's behavior.
            var taps = new[] { 1.0, 0.0 };
            var transform = new FirFilterTransform(channels: 1, taps);
            taps[0] = 999.0;

            var (outBuffer, _) = transform.Apply([100], frameCount: 1, channels: 1, SampleRate, bitsPerSample: 32);

            outBuffer[0].Should().Be(100); // still the original [1.0, 0.0] (identity), not the mutated value
        }

        // -------- IPcmTransform contract --------

        [Fact]
        public void Properties_Should_Indicate_A_PassThrough_Format_Transform()
        {
            var transform = new FirFilterTransform(channels: 2, [1.0]);

            transform.OutputSampleRate.Should().Be(0);
            transform.OutputChannels.Should().Be(0);
            transform.OutputBitsPerSample.Should().Be(0);
            transform.CanChangeFrameCount.Should().BeFalse();
        }

        [Fact]
        public void Apply_MismatchedChannelCount_Should_Throw()
        {
            var transform = new FirFilterTransform(channels: 2, [1.0]);
            var act = () => transform.Apply([1, 2, 3], frameCount: 1, channels: 3, SampleRate, bitsPerSample: 16);

            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Apply_ZeroFrameCount_Should_Return_Same_Buffer_Unchanged()
        {
            var transform = new FirFilterTransform(channels: 1, [1.0]);
            var buffer = new[] { 1, 2, 3 };

            var (outBuffer, frameCount) = transform.Apply(buffer, frameCount: 0, channels: 1, SampleRate, bitsPerSample: 16);

            frameCount.Should().Be(0);
            outBuffer.Should().BeSameAs(buffer);
        }

        // -------- DSP correctness --------

        [Fact]
        public void Apply_IdentityTap_Should_Be_A_NoOp()
        {
            var transform = new FirFilterTransform(channels: 1, [1.0]);
            var buffer = new[] { 100, -200, 32000, -32768, 0 };

            var (outBuffer, _) = transform.Apply((int[])buffer.Clone(), buffer.Length, channels: 1, SampleRate, bitsPerSample: 16);

            outBuffer.Should().Equal(buffer);
        }

        [Fact]
        public void Apply_TwoTapMovingAverage_Should_Match_HandComputed_Output()
        {
            // y[n] = 0.5*x[n] + 0.5*x[n-1], with x[-1] (the initial history) implicitly 0.
            var transform = new FirFilterTransform(channels: 1, [0.5, 0.5]);
            var buffer = new[] { 10, 20, 30, 40 };

            var (outBuffer, _) = transform.Apply(buffer, buffer.Length, channels: 1, SampleRate, bitsPerSample: 16);

            // y0 = 0.5*10 + 0.5*0  = 5
            // y1 = 0.5*20 + 0.5*10 = 15
            // y2 = 0.5*30 + 0.5*20 = 25
            // y3 = 0.5*40 + 0.5*30 = 35
            outBuffer.Should().Equal(5, 15, 25, 35);
        }

        [Fact]
        public void Apply_ThreeTapFilter_Should_Match_HandComputed_Output_Including_History_Depth_Two()
        {
            // y[n] = x[n] - x[n-2] (a simple first-difference-at-lag-2 filter), exercising a history
            // longer than one sample.
            var transform = new FirFilterTransform(channels: 1, [1.0, 0.0, -1.0]);
            var buffer = new[] { 10, 20, 30, 40, 50 };

            var (outBuffer, _) = transform.Apply(buffer, buffer.Length, channels: 1, SampleRate, bitsPerSample: 32);

            // y0 = x0 - x(-2) = 10 - 0  = 10
            // y1 = x1 - x(-1) = 20 - 0  = 20
            // y2 = x2 - x0    = 30 - 10 = 20
            // y3 = x3 - x1    = 40 - 20 = 20
            // y4 = x4 - x2    = 50 - 30 = 20
            outBuffer.Should().Equal(10, 20, 20, 20, 20);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(7)]
        public void Apply_ChunkedAcrossMultipleCalls_Should_Match_SingleShot_Processing(int chunkSize)
        {
            var taps = new[] { 0.2, 0.3, -0.1, 0.25, 0.15 }; // arbitrary 5-tap filter, history depth 4
            int[] buffer = [100, -50, 2000, -3000, 500, 1234, -4321, 777, -999, 10, 20, 30];

            var singleShotTransform = new FirFilterTransform(channels: 1, taps);
            var (singleShotOutput, _) = singleShotTransform.Apply((int[])buffer.Clone(), buffer.Length, channels: 1, SampleRate, bitsPerSample: 32);

            var chunkedTransform = new FirFilterTransform(channels: 1, taps);
            var chunkedOutput = new int[buffer.Length];
            var offset = 0;
            while (offset < buffer.Length)
            {
                var thisChunkSize = Math.Min(chunkSize, buffer.Length - offset);
                var chunk = buffer[offset..(offset + thisChunkSize)];
                var (chunkOutput, chunkFrameCount) = chunkedTransform.Apply(chunk, thisChunkSize, channels: 1, SampleRate, bitsPerSample: 32);
                Array.Copy(chunkOutput, 0, chunkedOutput, offset, chunkFrameCount);
                offset += thisChunkSize;
            }

            chunkedOutput.Should().Equal(singleShotOutput);
        }

        [Fact]
        public void Apply_MultiChannel_Should_Filter_Each_Channel_Independently()
        {
            var transform = new FirFilterTransform(channels: 2, [0.5, 0.5]);
            // Left: 10, 20. Right: 1000, -1000.
            var buffer = new[] { 10, 1000, 20, -1000 };

            var (outBuffer, _) = transform.Apply(buffer, frameCount: 2, channels: 2, SampleRate, bitsPerSample: 32);

            // Left:  y0 = 0.5*10+0.5*0=5,    y1 = 0.5*20+0.5*10=15
            // Right: y0 = 0.5*1000+0.5*0=500, y1 = 0.5*(-1000)+0.5*1000=0
            outBuffer.Should().Equal(5, 500, 15, 0);
        }

        [Fact]
        public void Apply_Clamps_To_Native_Range_Instead_Of_Wrapping()
        {
            // A gain-of-10 single tap pushed well past 16-bit range must clamp, not wrap, on write.
            var transform = new FirFilterTransform(channels: 1, [10.0]);

            var (outBuffer, _) = transform.Apply([10000, -10000], frameCount: 2, channels: 1, SampleRate, bitsPerSample: 16);

            outBuffer.Should().Equal(32767, -32768);
        }

        [Fact]
        public void Apply_OpposingExtremeTaps_Should_Produce_Silence_Not_An_Unspecified_Value()
        {
            // Each tap individually is finite (passes construction validation), but tap0*x[1] overflows to
            // +Infinity while tap1*x[0] overflows to -Infinity for a large enough sample, and their sum
            // (computed for the second output sample, once x[0] is in play via the lag-1 tap) is
            // Infinity + -Infinity = NaN. Math.Clamp passes NaN through unchanged, so without the explicit
            // NaN guard in Apply(), this would reach an unspecified-by-spec (int) cast instead of the
            // defined, intentional silence this codebase's other NaN-in-audio handling degrades to (see
            // FloatSampleConverter). Asserting 0 here locks in that defined behavior; note this test can't
            // actually distinguish "the guard ran" from "the unspecified cast happened to also produce 0"
            // on any one .NET build/platform (verified: it still passes with the guard removed, on this
            // SDK) -- the guard is worth keeping regardless, since spec-unspecified behavior isn't safe to
            // depend on across .NET versions or architectures even where it's currently convenient.
            var transform = new FirFilterTransform(channels: 1, [1e299, -1e299]);

            var (outBuffer, _) = transform.Apply([2_000_000_000, 2_000_000_000], frameCount: 2, channels: 1, SampleRate, bitsPerSample: 32);

            outBuffer[1].Should().Be(0);
        }

        // -------- Reset --------

        [Fact]
        public void Reset_Should_Clear_History()
        {
            var transform = new FirFilterTransform(channels: 1, [0.5, 0.5]);
            transform.Apply([20000], frameCount: 1, channels: 1, SampleRate, bitsPerSample: 16);

            transform.Reset();

            // Immediately after Reset, a silent sample should produce silence -- no leftover history from
            // the warmup sample above driving the output, which would not be true without clearing state.
            var (outBuffer, _) = transform.Apply([0], frameCount: 1, channels: 1, SampleRate, bitsPerSample: 16);

            outBuffer[0].Should().Be(0);
        }
    }
}
