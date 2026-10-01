using EggEncoder.Pcm;
using FluentAssertions;

namespace EggEncoder.UnitTests.Pcm
{
    public class ButterworthTransformTest
    {
        private const int SampleRate = 44100;

        // -------- Construction / validation --------

        [Theory]
        [InlineData(BiquadFilterType.BandPass)]
        [InlineData(BiquadFilterType.Notch)]
        [InlineData(BiquadFilterType.AllPass)]
        [InlineData(BiquadFilterType.PeakingEq)]
        [InlineData(BiquadFilterType.LowShelf)]
        [InlineData(BiquadFilterType.HighShelf)]
        public void Constructor_UnsupportedFilterType_Should_Throw(BiquadFilterType filterType)
        {
            var act = () => new ButterworthTransform(filterType, order: 4, channels: 1, SampleRate, frequencyHz: 1000);

            act.Should().Throw<ArgumentException>().And.ParamName.Should().Be("filterType");
        }

        [Theory]
        [InlineData(BiquadFilterType.LowPass)]
        [InlineData(BiquadFilterType.HighPass)]
        public void Constructor_SupportedFilterType_Should_Not_Throw(BiquadFilterType filterType)
        {
            var act = () => new ButterworthTransform(filterType, order: 4, channels: 1, SampleRate, frequencyHz: 1000);

            act.Should().NotThrow();
        }

        [Fact]
        public void Constructor_ZeroOrder_Should_Throw()
        {
            var act = () => new ButterworthTransform(BiquadFilterType.LowPass, order: 0, channels: 1, SampleRate, frequencyHz: 1000);

            act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("order");
        }

        [Fact]
        public void Constructor_NegativeOrder_Should_Throw()
        {
            var act = () => new ButterworthTransform(BiquadFilterType.LowPass, order: -2, channels: 1, SampleRate, frequencyHz: 1000);

            act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("order");
        }

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(5)]
        [InlineData(7)]
        public void Constructor_OddOrder_Should_Throw(int order)
        {
            var act = () => new ButterworthTransform(BiquadFilterType.LowPass, order, channels: 1, SampleRate, frequencyHz: 1000);

            act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("order");
        }

        [Theory]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(6)]
        [InlineData(8)]
        public void Constructor_PositiveEvenOrder_Should_Not_Throw(int order)
        {
            var act = () => new ButterworthTransform(BiquadFilterType.LowPass, order, channels: 1, SampleRate, frequencyHz: 1000);

            act.Should().NotThrow();
        }

        [Fact]
        public void Constructor_OrderAtMax_Should_Not_Throw()
        {
            var act = () => new ButterworthTransform(BiquadFilterType.LowPass, order: 64, channels: 1, SampleRate, frequencyHz: 1000);

            act.Should().NotThrow();
        }

        [Fact]
        public void Constructor_OrderAboveMax_Should_Throw()
        {
            // Must fail validation immediately rather than attempting to allocate/construct an
            // impractically large number of cascaded stages (also guards against this test itself
            // hanging/OOMing if the cap were ever accidentally removed).
            var act = () => new ButterworthTransform(BiquadFilterType.LowPass, order: 66, channels: 1, SampleRate, frequencyHz: 1000);

            act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("order");
        }

        [Fact]
        public void Constructor_PathologicallyLargeOrder_Should_Throw_Not_Hang_Or_OutOfMemory()
        {
            var act = () => new ButterworthTransform(BiquadFilterType.LowPass, order: 2_000_000_000, channels: 1, SampleRate, frequencyHz: 1000);

            act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("order");
        }

        [Fact]
        public void Constructor_ChannelsNonPositive_Should_Propagate_BiquadTransforms_Validation()
        {
            // No try/catch wrapping: the underlying BiquadTransform's own exception (type, paramName, and
            // message) should reach the caller unchanged, not get swallowed or rewrapped into something
            // generic.
            var act = () => new ButterworthTransform(BiquadFilterType.LowPass, order: 4, channels: 0, SampleRate, frequencyHz: 1000);

            act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("channels");
        }

        [Fact]
        public void Constructor_SampleRateNonPositive_Should_Propagate_BiquadTransforms_Validation()
        {
            var act = () => new ButterworthTransform(BiquadFilterType.LowPass, order: 4, channels: 1, sampleRate: 0, frequencyHz: 1000);

            act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("sampleRate");
        }

        [Fact]
        public void Constructor_FrequencyNonPositive_Should_Propagate_BiquadTransforms_Validation()
        {
            var act = () => new ButterworthTransform(BiquadFilterType.LowPass, order: 4, channels: 1, SampleRate, frequencyHz: 0);

            act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("frequencyHz");
        }

        [Fact]
        public void Constructor_FrequencyAtOrAboveNyquist_Should_Propagate_BiquadTransforms_Validation()
        {
            var act = () => new ButterworthTransform(BiquadFilterType.LowPass, order: 4, channels: 1, SampleRate, frequencyHz: SampleRate / 2.0);

            act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("frequencyHz");
        }

        // -------- IPcmTransform contract --------

        [Fact]
        public void Properties_Should_Indicate_A_PassThrough_Format_Transform()
        {
            var transform = new ButterworthTransform(BiquadFilterType.LowPass, order: 4, channels: 2, SampleRate, frequencyHz: 1000);

            transform.OutputSampleRate.Should().Be(0);
            transform.OutputChannels.Should().Be(0);
            transform.OutputBitsPerSample.Should().Be(0);
            transform.CanChangeFrameCount.Should().BeFalse();
        }

        [Fact]
        public void Apply_MismatchedChannelCount_Should_Throw()
        {
            // Not validated by ButterworthTransform itself -- propagates from the first cascaded stage's
            // own BiquadTransform.Apply() validation, same as the constructor-time propagation tests above.
            var transform = new ButterworthTransform(BiquadFilterType.LowPass, order: 4, channels: 2, SampleRate, frequencyHz: 1000);
            var act = () => transform.Apply([1, 2, 3], frameCount: 1, channels: 3, SampleRate, bitsPerSample: 16);

            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Apply_MismatchedSampleRate_Should_Throw()
        {
            var transform = new ButterworthTransform(BiquadFilterType.LowPass, order: 4, channels: 1, SampleRate, frequencyHz: 1000);
            var act = () => transform.Apply([1, 2, 3], frameCount: 3, channels: 1, sampleRate: 22050, bitsPerSample: 16);

            act.Should().Throw<ArgumentException>();
        }

        [Theory]
        [InlineData(2)]
        [InlineData(8)]
        public void Apply_ZeroFrameCount_Should_Return_Empty_Without_Throwing(int order)
        {
            var transform = new ButterworthTransform(BiquadFilterType.LowPass, order, channels: 1, SampleRate, frequencyHz: 1000);

            var (outBuffer, frameCount) = transform.Apply([], frameCount: 0, channels: 1, SampleRate, bitsPerSample: 16);

            frameCount.Should().Be(0);
            outBuffer.Should().BeEmpty();
        }

        // -------- Happy path --------

        [Theory]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(6)]
        public void Apply_LowPass_Should_Produce_Finite_InRange_Output(int order)
        {
            var transform = new ButterworthTransform(BiquadFilterType.LowPass, order, channels: 1, SampleRate, frequencyHz: 1000);
            var buffer = BuildSine(2000, frequencyHz: 500, amplitude: 20000);

            var (outBuffer, frameCount) = transform.Apply(buffer, buffer.Length, channels: 1, SampleRate, bitsPerSample: 16);

            frameCount.Should().Be(buffer.Length);
            outBuffer.Should().OnlyContain(sample => sample >= -32768 && sample <= 32767);
        }

        [Theory]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(6)]
        public void Apply_HighPass_Should_Produce_Finite_InRange_Output(int order)
        {
            var transform = new ButterworthTransform(BiquadFilterType.HighPass, order, channels: 1, SampleRate, frequencyHz: 1000);
            var buffer = BuildSine(2000, frequencyHz: 5000, amplitude: 20000);

            var (outBuffer, frameCount) = transform.Apply(buffer, buffer.Length, channels: 1, SampleRate, bitsPerSample: 16);

            frameCount.Should().Be(buffer.Length);
            outBuffer.Should().OnlyContain(sample => sample >= -32768 && sample <= 32767);
        }

        [Fact]
        public void Apply_Order2_Should_Produce_Identical_Output_To_A_Single_BiquadTransform_At_Default_Q()
        {
            // A 2nd-order Butterworth cascade is, by construction, exactly one biquad stage at
            // Q = 1/(2*cos(pi/4)) = 1/sqrt(2) -- BiquadTransform's own default Q. This is the strongest
            // possible correctness check for the cascade formula/wiring: it isn't just "close", the two
            // should compute bit-for-bit the same thing.
            var butterworth = new ButterworthTransform(BiquadFilterType.LowPass, order: 2, channels: 1, SampleRate, frequencyHz: 1000);
            var singleBiquad = new BiquadTransform(BiquadFilterType.LowPass, channels: 1, SampleRate, frequencyHz: 1000);

            var buffer = BuildSine(1000, frequencyHz: 300, amplitude: 15000);

            var (butterworthOutput, _) = butterworth.Apply((int[])buffer.Clone(), buffer.Length, channels: 1, SampleRate, bitsPerSample: 16);
            var (biquadOutput, _) = singleBiquad.Apply((int[])buffer.Clone(), buffer.Length, channels: 1, SampleRate, bitsPerSample: 16);

            butterworthOutput.Should().Equal(biquadOutput);
        }

        [Fact]
        public void Apply_Order2_HighPass_Should_Produce_Identical_Output_To_A_Single_BiquadTransform_At_Default_Q()
        {
            // Same equivalence as the LowPass case above, for the other supported filter type.
            var butterworth = new ButterworthTransform(BiquadFilterType.HighPass, order: 2, channels: 1, SampleRate, frequencyHz: 1000);
            var singleBiquad = new BiquadTransform(BiquadFilterType.HighPass, channels: 1, SampleRate, frequencyHz: 1000);

            var buffer = BuildSine(1000, frequencyHz: 300, amplitude: 15000);

            var (butterworthOutput, _) = butterworth.Apply((int[])buffer.Clone(), buffer.Length, channels: 1, SampleRate, bitsPerSample: 16);
            var (biquadOutput, _) = singleBiquad.Apply((int[])buffer.Clone(), buffer.Length, channels: 1, SampleRate, bitsPerSample: 16);

            butterworthOutput.Should().Equal(biquadOutput);
        }

        // -------- DSP correctness: exact closed-form DC/Nyquist gain --------
        //
        // Each cascaded LowPass/HighPass biquad stage has an exact DC/Nyquist gain of 1 or 0 regardless of
        // its own Q (proven in BiquadTransformTest). Cascading stageCount of them multiplies those exact
        // gains together, so a LowPass cascade's overall DC gain is 1^stageCount = 1 and its Nyquist gain
        // is 0^stageCount = 0 for any order -- and the mirror image for HighPass. This holds for every
        // order, so a wrong per-stage Q (e.g. a transposed cascade formula) is unlikely to still pass both
        // checks at multiple orders by coincidence.

        [Theory]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(6)]
        public void SteadyState_LowPass_DcGain_Should_Be_Exactly_One_NyquistGain_Should_Be_Exactly_Zero(int order)
        {
            var dcTransform = new ButterworthTransform(BiquadFilterType.LowPass, order, channels: 1, SampleRate, frequencyHz: 1000);
            var nyquistTransform = new ButterworthTransform(BiquadFilterType.LowPass, order, channels: 1, SampleRate, frequencyHz: 1000);

            MeasureDcGain(dcTransform).Should().BeApproximately(1.0, 0.01);
            MeasureNyquistGain(nyquistTransform).Should().BeApproximately(0.0, 0.01);
        }

        [Theory]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(6)]
        public void SteadyState_HighPass_DcGain_Should_Be_Exactly_Zero_NyquistGain_Should_Be_Exactly_One(int order)
        {
            var dcTransform = new ButterworthTransform(BiquadFilterType.HighPass, order, channels: 1, SampleRate, frequencyHz: 1000);
            var nyquistTransform = new ButterworthTransform(BiquadFilterType.HighPass, order, channels: 1, SampleRate, frequencyHz: 1000);

            MeasureDcGain(dcTransform).Should().BeApproximately(0.0, 0.01);
            MeasureNyquistGain(nyquistTransform).Should().BeApproximately(1.0, 0.01);
        }

        // -------- Multi-channel independence --------

        [Fact]
        public void Apply_MultiChannel_Should_Filter_Each_Channel_Independently()
        {
            var transform = new ButterworthTransform(BiquadFilterType.LowPass, order: 4, channels: 2, SampleRate, frequencyHz: 1000);

            const int frames = 2000;
            var buffer = new int[frames * 2];
            for (var frame = 0; frame < frames; frame++)
            {
                buffer[(frame * 2) + 0] = 10000; // left: DC -- a low-pass cascade should pass this at ~gain 1
                buffer[(frame * 2) + 1] = frame % 2 == 0 ? 10000 : -10000; // right: Nyquist -- blocked at ~gain 0
            }

            var (outBuffer, _) = transform.Apply(buffer, frames, channels: 2, SampleRate, bitsPerSample: 16);

            var lastLeft = outBuffer[((frames - 1) * 2) + 0];
            var lastRight = outBuffer[((frames - 1) * 2) + 1];

            lastLeft.Should().BeInRange(9500, 10000);
            Math.Abs(lastRight).Should().BeLessThan(500);
        }

        // -------- Reset --------

        [Fact]
        public void Reset_Should_Clear_Every_Cascaded_Stages_History()
        {
            var transform = new ButterworthTransform(BiquadFilterType.LowPass, order: 6, channels: 1, SampleRate, frequencyHz: 1000);
            var warmup = BuildSine(1000, frequencyHz: 300, amplitude: 20000);
            transform.Apply(warmup, warmup.Length, channels: 1, SampleRate, bitsPerSample: 16);

            transform.Reset();

            // Immediately after Reset, a single silent sample should produce silence (no leftover history
            // from any cascaded stage driving the output), which would not be true without clearing state.
            var (outBuffer, _) = transform.Apply([0], frameCount: 1, channels: 1, SampleRate, bitsPerSample: 16);

            outBuffer[0].Should().Be(0);
        }

        // -------- Cross-block state: chunked vs single-shot --------

        [Theory]
        [InlineData(2)]
        [InlineData(6)]
        public void Apply_ChunkedAcrossMultipleCalls_Should_Match_SingleShot_Processing(int order)
        {
            var buffer = BuildSine(3000, frequencyHz: 700, amplitude: 18000);

            var singleShotTransform = new ButterworthTransform(BiquadFilterType.LowPass, order, channels: 1, SampleRate, frequencyHz: 1000);
            var (singleShotOutput, _) = singleShotTransform.Apply((int[])buffer.Clone(), buffer.Length, channels: 1, SampleRate, bitsPerSample: 16);

            var chunkedTransform = new ButterworthTransform(BiquadFilterType.LowPass, order, channels: 1, SampleRate, frequencyHz: 1000);
            var chunkedOutput = new int[buffer.Length];
            const int chunkSize = 137; // deliberately not a divisor of buffer.Length, and not a power of two
            var offset = 0;
            while (offset < buffer.Length)
            {
                var thisChunkSize = Math.Min(chunkSize, buffer.Length - offset);
                var chunk = buffer[offset..(offset + thisChunkSize)];
                var (chunkOutput, chunkFrameCount) = chunkedTransform.Apply(chunk, thisChunkSize, channels: 1, SampleRate, bitsPerSample: 16);
                Array.Copy(chunkOutput, 0, chunkedOutput, offset, chunkFrameCount);
                offset += thisChunkSize;
            }

            // Each cascaded BiquadTransform stage is a pure per-sample recursive filter with no lookahead
            // or windowing, so chunking shouldn't change the result at all in principle; allow a small
            // per-sample tolerance rather than demanding bit-exact equality, since the task only promises
            // "within floating point tolerance".
            for (var i = 0; i < buffer.Length; i++)
            {
                Math.Abs(chunkedOutput[i] - singleShotOutput[i]).Should().BeLessOrEqualTo(1,
                    $"sample {i} should match within rounding tolerance regardless of how Apply() calls were chunked");
            }
        }

        // -------- Helpers --------

        private static int[] BuildSine(int frameCount, double frequencyHz, double amplitude)
        {
            var buffer = new int[frameCount];
            for (var i = 0; i < frameCount; i++)
                buffer[i] = (int)Math.Round(amplitude * Math.Sin(2 * Math.PI * frequencyHz * i / SampleRate));
            return buffer;
        }

        private static double MeasureDcGain(ButterworthTransform transform, int settleFrames = 2000, int input = 1_000_000)
        {
            var buffer = new int[settleFrames];
            for (var i = 0; i < settleFrames; i++) buffer[i] = input;

            var (outBuffer, _) = transform.Apply(buffer, settleFrames, channels: 1, SampleRate, bitsPerSample: 32);

            return outBuffer[settleFrames - 1] / (double)input;
        }

        private static double MeasureNyquistGain(ButterworthTransform transform, int settleFrames = 2000, int input = 1_000_000)
        {
            var buffer = new int[settleFrames];
            for (var i = 0; i < settleFrames; i++) buffer[i] = i % 2 == 0 ? input : -input;

            var (outBuffer, _) = transform.Apply(buffer, settleFrames, channels: 1, SampleRate, bitsPerSample: 32);

            // settleFrames is even, so the last index is odd -- expected sign matches the odd-index input (-input).
            return outBuffer[settleFrames - 1] / (double)(-input);
        }
    }
}
