using EggEncoder.Pcm;
using FluentAssertions;

namespace EggEncoder.UnitTests.Pcm
{
    public class BiquadTransformTest
    {
        private const int SampleRate = 44100;
        private const double DefaultQ = 0.7071067811865476;

        // -------- Construction / validation --------

        [Theory]
        [InlineData(BiquadFilterType.LowPass)]
        [InlineData(BiquadFilterType.HighPass)]
        [InlineData(BiquadFilterType.BandPass)]
        [InlineData(BiquadFilterType.Notch)]
        [InlineData(BiquadFilterType.AllPass)]
        [InlineData(BiquadFilterType.PeakingEq)]
        [InlineData(BiquadFilterType.LowShelf)]
        [InlineData(BiquadFilterType.HighShelf)]
        public void Constructor_EveryFilterType_Should_Not_Throw_On_Reasonable_Arguments(BiquadFilterType filterType)
        {
            // Gain-bearing types (PeakingEq/LowShelf/HighShelf) accept a non-zero gain; every other type
            // requires gainDb == 0 (see Constructor_NonZeroGainForNonGainFilterType_Should_Throw), so this
            // uses 0 uniformly and leaves exercising non-zero gain to the dedicated tests below.
            var act = () => new BiquadTransform(filterType, channels: 2, SampleRate, frequencyHz: 1000, DefaultQ, gainDb: 0.0);

            act.Should().NotThrow();
        }

        [Fact]
        public void Constructor_ChannelsNonPositive_Should_Throw()
        {
            var act = () => new BiquadTransform(BiquadFilterType.LowPass, channels: 0, SampleRate, frequencyHz: 1000);

            act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("channels");
        }

        [Fact]
        public void Constructor_SampleRateNonPositive_Should_Throw()
        {
            var act = () => new BiquadTransform(BiquadFilterType.LowPass, channels: 1, sampleRate: 0, frequencyHz: 1000);

            act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("sampleRate");
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-100.0)]
        public void Constructor_FrequencyNonPositive_Should_Throw(double frequencyHz)
        {
            var act = () => new BiquadTransform(BiquadFilterType.LowPass, channels: 1, SampleRate, frequencyHz);

            act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("frequencyHz");
        }

        [Fact]
        public void Constructor_FrequencyAtNyquist_Should_Throw()
        {
            var act = () => new BiquadTransform(BiquadFilterType.LowPass, channels: 1, SampleRate, frequencyHz: SampleRate / 2.0);

            act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("frequencyHz");
        }

        [Fact]
        public void Constructor_FrequencyAboveNyquist_Should_Throw()
        {
            var act = () => new BiquadTransform(BiquadFilterType.LowPass, channels: 1, SampleRate, frequencyHz: SampleRate);

            act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("frequencyHz");
        }

        [Fact]
        public void Constructor_FrequencyJustBelowNyquist_Should_Not_Throw()
        {
            var act = () => new BiquadTransform(BiquadFilterType.LowPass, channels: 1, SampleRate, frequencyHz: (SampleRate / 2.0) - 1);

            act.Should().NotThrow();
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        public void Constructor_QNonPositive_Should_Throw(double q)
        {
            var act = () => new BiquadTransform(BiquadFilterType.LowPass, channels: 1, SampleRate, frequencyHz: 1000, q);

            act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("q");
        }

        [Theory]
        [InlineData(BiquadFilterType.LowShelf)]
        [InlineData(BiquadFilterType.HighShelf)]
        public void Constructor_ShelfSlopeAboveOne_Should_Throw(BiquadFilterType filterType)
        {
            var act = () => new BiquadTransform(filterType, channels: 1, SampleRate, frequencyHz: 1000, q: 1.0001, gainDb: 6.0);

            act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("q");
        }

        [Theory]
        [InlineData(BiquadFilterType.LowShelf)]
        [InlineData(BiquadFilterType.HighShelf)]
        public void Constructor_ShelfSlopeOfExactlyOne_Should_Not_Throw(BiquadFilterType filterType)
        {
            var act = () => new BiquadTransform(filterType, channels: 1, SampleRate, frequencyHz: 1000, q: 1.0, gainDb: 6.0);

            act.Should().NotThrow();
        }

        [Theory]
        [InlineData(BiquadFilterType.LowPass)]
        [InlineData(BiquadFilterType.HighPass)]
        [InlineData(BiquadFilterType.BandPass)]
        [InlineData(BiquadFilterType.Notch)]
        [InlineData(BiquadFilterType.AllPass)]
        public void Constructor_NonZeroGainForNonGainFilterType_Should_Throw(BiquadFilterType filterType)
        {
            var act = () => new BiquadTransform(filterType, channels: 1, SampleRate, frequencyHz: 1000, DefaultQ, gainDb: 3.0);

            act.Should().Throw<ArgumentException>().And.ParamName.Should().Be("gainDb");
        }

        [Fact]
        public void Constructor_ZeroGainForNonGainFilterType_Should_Not_Throw()
        {
            var act = () => new BiquadTransform(BiquadFilterType.LowPass, channels: 1, SampleRate, frequencyHz: 1000, DefaultQ, gainDb: 0.0);

            act.Should().NotThrow();
        }

        [Fact]
        public void Constructor_PeakingEqWithNonZeroGain_Should_Not_Throw()
        {
            var act = () => new BiquadTransform(BiquadFilterType.PeakingEq, channels: 1, SampleRate, frequencyHz: 1000, DefaultQ, gainDb: -6.0);

            act.Should().NotThrow();
        }

        [Fact]
        public void Constructor_NaNFrequency_Should_Throw()
        {
            var act = () => new BiquadTransform(BiquadFilterType.LowPass, channels: 1, SampleRate, frequencyHz: double.NaN);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Constructor_InfiniteGain_Should_Throw()
        {
            var act = () => new BiquadTransform(BiquadFilterType.PeakingEq, channels: 1, SampleRate, frequencyHz: 1000, DefaultQ, gainDb: double.PositiveInfinity);

            act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("gainDb");
        }

        [Fact]
        public void Constructor_NegativeInfiniteGain_Should_Throw()
        {
            var act = () => new BiquadTransform(BiquadFilterType.PeakingEq, channels: 1, SampleRate, frequencyHz: 1000, DefaultQ, gainDb: double.NegativeInfinity);

            act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("gainDb");
        }

        [Fact]
        public void Constructor_NaNQ_Should_Throw()
        {
            var act = () => new BiquadTransform(BiquadFilterType.LowPass, channels: 1, SampleRate, frequencyHz: 1000, q: double.NaN);

            act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("q");
        }

        [Fact]
        public void Constructor_NaNGain_OnGainBearingType_Should_Throw()
        {
            // For a non-gain type, a non-zero (including NaN, since NaN != 0 is true) gain is caught
            // earlier by the gainDb-only-applies-to-gain-types check; this exercises the dedicated
            // NaN/Infinity check specifically, which only a gain-bearing type's NaN gain can reach.
            var act = () => new BiquadTransform(BiquadFilterType.PeakingEq, channels: 1, SampleRate, frequencyHz: 1000, DefaultQ, gainDb: double.NaN);

            act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("gainDb");
        }

        [Fact]
        public void Constructor_InvalidFilterTypeEnumValue_Should_Throw()
        {
            var act = () => new BiquadTransform((BiquadFilterType)99, channels: 1, SampleRate, frequencyHz: 1000);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Constructor_PathologicallyTinyQ_Should_Throw_Rather_Than_Silently_Produce_NonFinite_Coefficients()
        {
            // 1/q can overflow double's representable range for a small enough positive q, which (after
            // normalizing coefficients by a0) can divide Infinity by Infinity and produce NaN -- silently
            // corrupting every sample Apply() ever processes afterward instead of failing fast here.
            var act = () => new BiquadTransform(BiquadFilterType.BandPass, channels: 1, SampleRate, frequencyHz: 1000, q: double.Epsilon);

            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Constructor_PathologicallyTinyShelfSlope_Should_Throw_Rather_Than_Silently_Produce_NonFinite_Coefficients()
        {
            var act = () => new BiquadTransform(BiquadFilterType.LowShelf, channels: 1, SampleRate, frequencyHz: 1000, q: double.Epsilon, gainDb: 6.0);

            act.Should().Throw<ArgumentException>();
        }

        // -------- IPcmTransform contract --------

        [Fact]
        public void Properties_Should_Indicate_A_PassThrough_Format_Transform()
        {
            var transform = new BiquadTransform(BiquadFilterType.LowPass, channels: 2, SampleRate, frequencyHz: 1000);

            transform.OutputChannels.Should().Be(0);
            transform.OutputSampleRate.Should().Be(0);
            transform.OutputBitsPerSample.Should().Be(0);
            transform.CanChangeFrameCount.Should().BeFalse();
        }

        [Fact]
        public void Apply_MismatchedChannelCount_Should_Throw()
        {
            var transform = new BiquadTransform(BiquadFilterType.LowPass, channels: 2, SampleRate, frequencyHz: 1000);
            var act = () => transform.Apply([1, 2, 3], frameCount: 1, channels: 3, SampleRate, bitsPerSample: 16);

            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Apply_MismatchedSampleRate_Should_Throw()
        {
            var transform = new BiquadTransform(BiquadFilterType.LowPass, channels: 1, SampleRate, frequencyHz: 1000);
            var act = () => transform.Apply([1, 2, 3], frameCount: 3, channels: 1, sampleRate: 22050, bitsPerSample: 16);

            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Apply_ZeroFrameCount_Should_Return_Empty_Without_Throwing()
        {
            var transform = new BiquadTransform(BiquadFilterType.LowPass, channels: 1, SampleRate, frequencyHz: 1000);

            var (outBuffer, frameCount) = transform.Apply([], frameCount: 0, channels: 1, SampleRate, bitsPerSample: 16);

            frameCount.Should().Be(0);
            outBuffer.Should().BeEmpty();
        }

        // -------- Exact closed-form steady-state gain (DC and Nyquist) --------
        //
        // For a stable LTI biquad H(z) = (b0 + b1*z^-1 + b2*z^-2) / (1 + a1*z^-1 + a2*z^-2), feeding a
        // constant input converges to a steady-state output of input * H(1) = input * (b0+b1+b2)/(1+a1+a2)
        // ("DC gain"). Feeding an alternating +v/-v input (exactly the Nyquist frequency, z = -1)
        // converges to steady-state output of +/-v * H(-1) = +/-v * (b0-b1+b2)/(1-a1+a2) ("Nyquist gain").
        // These ratios are known in closed form for every RBJ filter type (worked out from the cookbook
        // formulas), independent of frequency/Q, which makes them an exact, cheap way to verify each
        // filter type's coefficients are right -- a transposed sign or swapped formula would very likely
        // fail at least one of these.

        [Theory]
        [InlineData(BiquadFilterType.LowPass, 1.0, 0.0)]
        [InlineData(BiquadFilterType.HighPass, 0.0, 1.0)]
        [InlineData(BiquadFilterType.BandPass, 0.0, 0.0)]
        [InlineData(BiquadFilterType.Notch, 1.0, 1.0)]
        [InlineData(BiquadFilterType.AllPass, 1.0, 1.0)]
        public void SteadyState_DcAndNyquistGain_Should_Match_Closed_Form(BiquadFilterType filterType, double expectedDcGain, double expectedNyquistGain)
        {
            var transform = new BiquadTransform(filterType, channels: 1, SampleRate, frequencyHz: 1000, DefaultQ);

            var dcGain = MeasureDcGain(transform);
            var nyquistGain = MeasureNyquistGain(new BiquadTransform(filterType, channels: 1, SampleRate, frequencyHz: 1000, DefaultQ));

            dcGain.Should().BeApproximately(expectedDcGain, 0.01);
            nyquistGain.Should().BeApproximately(expectedNyquistGain, 0.01);
        }

        [Fact]
        public void SteadyState_PeakingEq_DcAndNyquistGain_Should_Be_Unity_Regardless_Of_GainDb()
        {
            // PeakingEq only boosts/cuts near its center frequency; far from it (DC and Nyquist), gain
            // is always exactly 1 regardless of gainDb.
            var boost = new BiquadTransform(BiquadFilterType.PeakingEq, channels: 1, SampleRate, frequencyHz: 1000, DefaultQ, gainDb: 12.0);
            var cut = new BiquadTransform(BiquadFilterType.PeakingEq, channels: 1, SampleRate, frequencyHz: 1000, DefaultQ, gainDb: -12.0);

            MeasureDcGain(boost).Should().BeApproximately(1.0, 0.01);
            MeasureDcGain(cut).Should().BeApproximately(1.0, 0.01);
            MeasureNyquistGain(new BiquadTransform(BiquadFilterType.PeakingEq, channels: 1, SampleRate, frequencyHz: 1000, DefaultQ, gainDb: 12.0)).Should().BeApproximately(1.0, 0.01);
        }

        [Theory]
        [InlineData(12.0)]
        [InlineData(-12.0)]
        public void SteadyState_LowShelf_DcGain_Should_Equal_LinearGainSquared_NyquistGain_Should_Be_Unity(double gainDb)
        {
            var expectedDcGain = Math.Pow(Math.Pow(10, gainDb / 40.0), 2); // A^2, derived from the cookbook's low-shelf coefficients
            var transform = new BiquadTransform(BiquadFilterType.LowShelf, channels: 1, SampleRate, frequencyHz: 1000, q: 1.0, gainDb);

            MeasureDcGain(transform).Should().BeApproximately(expectedDcGain, expectedDcGain * 0.02);
            MeasureNyquistGain(new BiquadTransform(BiquadFilterType.LowShelf, channels: 1, SampleRate, frequencyHz: 1000, q: 1.0, gainDb)).Should().BeApproximately(1.0, 0.01);
        }

        [Theory]
        [InlineData(12.0)]
        [InlineData(-12.0)]
        public void SteadyState_HighShelf_NyquistGain_Should_Equal_LinearGainSquared_DcGain_Should_Be_Unity(double gainDb)
        {
            var expectedNyquistGain = Math.Pow(Math.Pow(10, gainDb / 40.0), 2); // A^2, derived from the cookbook's high-shelf coefficients
            var transform = new BiquadTransform(BiquadFilterType.HighShelf, channels: 1, SampleRate, frequencyHz: 1000, q: 1.0, gainDb);

            MeasureDcGain(transform).Should().BeApproximately(1.0, 0.01);
            MeasureNyquistGain(new BiquadTransform(BiquadFilterType.HighShelf, channels: 1, SampleRate, frequencyHz: 1000, q: 1.0, gainDb)).Should().BeApproximately(expectedNyquistGain, expectedNyquistGain * 0.02);
        }

        // -------- Center-frequency behavior (RMS of a steady sinusoid at the filter's own frequency) --------

        [Fact]
        public void CenterFrequency_BandPass_Should_Pass_With_Near_Unity_Gain()
        {
            var transform = new BiquadTransform(BiquadFilterType.BandPass, channels: 1, SampleRate, frequencyHz: 1000, q: 2.0);

            MeasureGainAtFrequency(transform, testFrequencyHz: 1000).Should().BeApproximately(1.0, 0.1);
        }

        [Fact]
        public void CenterFrequency_Notch_Should_Be_Heavily_Attenuated()
        {
            var transform = new BiquadTransform(BiquadFilterType.Notch, channels: 1, SampleRate, frequencyHz: 1000, q: 4.0);

            MeasureGainAtFrequency(transform, testFrequencyHz: 1000).Should().BeLessThan(0.2);
        }

        [Fact]
        public void CenterFrequency_AllPass_Should_Have_Unity_Gain()
        {
            var transform = new BiquadTransform(BiquadFilterType.AllPass, channels: 1, SampleRate, frequencyHz: 1000, q: 2.0);

            MeasureGainAtFrequency(transform, testFrequencyHz: 1000).Should().BeApproximately(1.0, 0.1);
        }

        [Fact]
        public void CenterFrequency_PeakingEq_Boost_Should_Increase_Gain()
        {
            var neutral = new BiquadTransform(BiquadFilterType.PeakingEq, channels: 1, SampleRate, frequencyHz: 1000, q: 1.0, gainDb: 0.0);
            var boosted = new BiquadTransform(BiquadFilterType.PeakingEq, channels: 1, SampleRate, frequencyHz: 1000, q: 1.0, gainDb: 12.0);

            var neutralGain = MeasureGainAtFrequency(neutral, testFrequencyHz: 1000);
            var boostedGain = MeasureGainAtFrequency(boosted, testFrequencyHz: 1000);

            neutralGain.Should().BeApproximately(1.0, 0.1);
            boostedGain.Should().BeGreaterThan(neutralGain * 2); // +12dB ~= 4x linear amplitude
        }

        [Fact]
        public void CenterFrequency_PeakingEq_Cut_Should_Decrease_Gain()
        {
            var cut = new BiquadTransform(BiquadFilterType.PeakingEq, channels: 1, SampleRate, frequencyHz: 1000, q: 1.0, gainDb: -12.0);

            MeasureGainAtFrequency(cut, testFrequencyHz: 1000).Should().BeLessThan(0.5);
        }

        // -------- Edge cases --------

        [Fact]
        public void Apply_ExtremelyHighQ_Should_Produce_Finite_InRange_Output_Without_Throwing()
        {
            // (A single Apply() call, not two: this transform is stateful across calls, so calling it
            // twice would check the second call's output -- built on the first call's leftover filter
            // history -- rather than verifying one coherent pass through the signal.)
            var transform = new BiquadTransform(BiquadFilterType.BandPass, channels: 1, SampleRate, frequencyHz: 1000, q: 1000.0);
            var buffer = new int[2000];
            for (var i = 0; i < buffer.Length; i++)
                buffer[i] = (int)Math.Round(20000 * Math.Sin(2 * Math.PI * 1000 * i / SampleRate));

            (int[] outBuffer, int frameCount) result = default;
            var act = () => result = transform.Apply(buffer, buffer.Length, channels: 1, SampleRate, bitsPerSample: 16);

            act.Should().NotThrow();
            result.outBuffer.Should().OnlyContain(sample => sample >= -32768 && sample <= 32767);
        }

        [Fact]
        public void Apply_ExtremelyLowQ_Should_Produce_Finite_InRange_Output_Without_Throwing()
        {
            var transform = new BiquadTransform(BiquadFilterType.LowPass, channels: 1, SampleRate, frequencyHz: 1000, q: 0.001);
            var buffer = new int[500];
            for (var i = 0; i < buffer.Length; i++)
                buffer[i] = (int)Math.Round(20000 * Math.Sin(2 * Math.PI * 500 * i / SampleRate));

            var (outBuffer, _) = transform.Apply(buffer, buffer.Length, channels: 1, SampleRate, bitsPerSample: 16);

            outBuffer.Should().OnlyContain(sample => sample >= -32768 && sample <= 32767);
        }

        [Fact]
        public void Apply_FrequencyExtremelyCloseToNyquist_Should_Produce_Finite_InRange_Output()
        {
            var nearNyquist = (SampleRate / 2.0) - 0.5;
            var transform = new BiquadTransform(BiquadFilterType.LowPass, channels: 1, SampleRate, frequencyHz: nearNyquist);
            var buffer = new int[500];
            for (var i = 0; i < buffer.Length; i++)
                buffer[i] = i % 2 == 0 ? 20000 : -20000;

            var (outBuffer, _) = transform.Apply(buffer, buffer.Length, channels: 1, SampleRate, bitsPerSample: 16);

            outBuffer.Should().OnlyContain(sample => sample >= -32768 && sample <= 32767);
        }

        [Fact]
        public void Apply_ExtremeGain_Should_Clamp_To_Native_Range_Instead_Of_Wrapping()
        {
            var transform = new BiquadTransform(BiquadFilterType.PeakingEq, channels: 1, SampleRate, frequencyHz: 1000, q: 1.0, gainDb: 48.0);
            var buffer = new int[2000];
            for (var i = 0; i < buffer.Length; i++)
                buffer[i] = (int)Math.Round(30000 * Math.Sin(2 * Math.PI * 1000 * i / SampleRate));

            var (outBuffer, _) = transform.Apply(buffer, buffer.Length, channels: 1, SampleRate, bitsPerSample: 16);

            outBuffer.Should().OnlyContain(sample => sample >= -32768 && sample <= 32767);
        }

        // -------- Multi-channel independence --------

        [Fact]
        public void Apply_MultiChannel_Should_Filter_Each_Channel_Independently()
        {
            // Left channel: DC (should pass through a low-pass at gain 1). Right channel: alternating
            // Nyquist-rate signal (should be blocked by the same low-pass, gain 0). If channel state were
            // shared instead of per-channel, one channel's history would leak into the other's output.
            var transform = new BiquadTransform(BiquadFilterType.LowPass, channels: 2, SampleRate, frequencyHz: 1000, DefaultQ);

            const int frames = 2000;
            var buffer = new int[frames * 2];
            for (var frame = 0; frame < frames; frame++)
            {
                buffer[(frame * 2) + 0] = 10000; // left: DC
                buffer[(frame * 2) + 1] = frame % 2 == 0 ? 10000 : -10000; // right: Nyquist
            }

            var (outBuffer, _) = transform.Apply(buffer, frames, channels: 2, SampleRate, bitsPerSample: 16);

            var lastLeft = outBuffer[((frames - 1) * 2) + 0];
            var lastRight = outBuffer[((frames - 1) * 2) + 1];

            lastLeft.Should().BeInRange(9500, 10000); // DC passes through a low-pass at ~gain 1
            Math.Abs(lastRight).Should().BeLessThan(500); // Nyquist is blocked by a low-pass at ~gain 0
        }

        // -------- Reset --------

        [Fact]
        public void Reset_Should_Clear_Filter_History()
        {
            var transform = new BiquadTransform(BiquadFilterType.LowPass, channels: 1, SampleRate, frequencyHz: 1000, DefaultQ);
            var warmup = new int[500];
            for (var i = 0; i < warmup.Length; i++) warmup[i] = 20000;
            transform.Apply(warmup, warmup.Length, channels: 1, SampleRate, bitsPerSample: 16);

            transform.Reset();

            // Immediately after Reset, a single silent sample should produce silence (no leftover history
            // driving the output), which would not be true without clearing state after the warmup above.
            var (outBuffer, _) = transform.Apply([0], frameCount: 1, channels: 1, SampleRate, bitsPerSample: 16);

            outBuffer[0].Should().Be(0);
        }

        // -------- Helpers --------

        private static double MeasureDcGain(BiquadTransform transform, int settleFrames = 2000, int input = 1_000_000)
        {
            var buffer = new int[settleFrames];
            for (var i = 0; i < settleFrames; i++) buffer[i] = input;

            var (outBuffer, _) = transform.Apply(buffer, settleFrames, channels: 1, SampleRate, bitsPerSample: 32);

            return outBuffer[settleFrames - 1] / (double)input;
        }

        private static double MeasureNyquistGain(BiquadTransform transform, int settleFrames = 2000, int input = 1_000_000)
        {
            var buffer = new int[settleFrames];
            for (var i = 0; i < settleFrames; i++) buffer[i] = i % 2 == 0 ? input : -input;

            var (outBuffer, _) = transform.Apply(buffer, settleFrames, channels: 1, SampleRate, bitsPerSample: 32);

            // settleFrames is even, so the last index is odd -- expected sign matches the odd-index input (-input).
            return outBuffer[settleFrames - 1] / (double)(-input);
        }

        private static double MeasureGainAtFrequency(BiquadTransform transform, double testFrequencyHz, int totalSamples = 20000, int discardSamples = 15000)
        {
            const double amplitude = 1_000_000.0;
            var buffer = new int[totalSamples];
            for (var i = 0; i < totalSamples; i++)
                buffer[i] = (int)Math.Round(amplitude * Math.Sin(2 * Math.PI * testFrequencyHz * i / SampleRate));

            var (outBuffer, _) = transform.Apply(buffer, totalSamples, channels: 1, SampleRate, bitsPerSample: 32);

            var count = totalSamples - discardSamples;
            var sumSquares = 0.0;
            for (var i = discardSamples; i < totalSamples; i++)
                sumSquares += (double)outBuffer[i] * outBuffer[i];

            var outputRms = Math.Sqrt(sumSquares / count);
            var inputRms = amplitude / Math.Sqrt(2);

            return outputRms / inputRms;
        }
    }
}
