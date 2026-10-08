using EggEncoder.Waveform;
using FluentAssertions;

namespace EggEncoder.UnitTests.Waveform
{
    public class WaveformCalculatorTest
    {
        [Fact]
        public void AddBlock_WithSilence_Should_Produce_AllZero_Windows()
        {
            var calculator = new WaveformCalculator(totalSamplesPerChannel: 2000, channels: 2, bitsPerSample: 16, windowCount: 10);

            calculator.AddBlock(new int[2000 * 2]);
            var windows = calculator.GetNormalizedWindows();

            windows.Should().HaveCount(10);
            windows.TrueForAll(window => window == 0).Should().BeTrue();
        }

        [Fact]
        public void AddBlock_WithFullScaleSamples_Should_Normalize_To_One()
        {
            var calculator = new WaveformCalculator(totalSamplesPerChannel: 100, channels: 1, bitsPerSample: 16, windowCount: 5);
            var fullScaleBlock = Enumerable.Repeat((int)short.MaxValue, 100).ToArray();
            var expectedPeak = (double)short.MaxValue / (1 << 15);

            calculator.AddBlock(fullScaleBlock);
            var windows = calculator.GetNormalizedWindows();

            windows.Should().HaveCount(5);
            windows.TrueForAll(window => window == expectedPeak).Should().BeTrue();
        }

        [Fact]
        public void AddBlock_SplitAcrossMultipleCalls_Should_Produce_Same_Result_As_SingleCall()
        {
            var random = new Random(42);
            var samples = new int[4000];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = random.Next(short.MinValue, short.MaxValue);
            }

            var singleCallCalculator = new WaveformCalculator(totalSamplesPerChannel: 2000, channels: 2, bitsPerSample: 16, windowCount: 20);
            singleCallCalculator.AddBlock(samples);
            var singleCallWindows = singleCallCalculator.GetNormalizedWindows();

            var multiCallCalculator = new WaveformCalculator(totalSamplesPerChannel: 2000, channels: 2, bitsPerSample: 16, windowCount: 20);
            const int framesPerChunk = 37;
            foreach (var chunk in samples.Chunk(framesPerChunk * 2))
            {
                multiCallCalculator.AddBlock(chunk);
            }

            var multiCallWindows = multiCallCalculator.GetNormalizedWindows();

            multiCallWindows.Should().Equal(singleCallWindows);
        }

        [Fact]
        public void GetNormalizedWindows_WithFewerSamplesThanWindowCount_Should_Not_Throw()
        {
            var calculator = new WaveformCalculator(totalSamplesPerChannel: 5, channels: 1, bitsPerSample: 16, windowCount: 200);

            calculator.AddBlock([100, 200, 300, 400, 500]);
            var windows = calculator.GetNormalizedWindows();

            windows.Should().HaveCount(5);
        }

        [Fact]
        public void GetNormalizedPeakAmplitude_BeforeAnyBlock_Should_Be_Zero()
        {
            var calculator = new WaveformCalculator(totalSamplesPerChannel: 100, channels: 1, bitsPerSample: 16);

            calculator.GetNormalizedPeakAmplitude().Should().Be(0.0);
        }

        [Fact]
        public void GetNormalizedPeakAmplitude_Should_Return_The_Loudest_AbsoluteSample_Across_AllChannelsAndBlocks()
        {
            var calculator = new WaveformCalculator(totalSamplesPerChannel: 3, channels: 2, bitsPerSample: 16);

            calculator.AddBlock([100, -200, 300, -32768]); // frame 0: ch0=100, ch1=-200; frame 1: ch0=300, ch1=-32768
            calculator.AddBlock([50, -60]); // a second block, split across calls -- must still be seen

            calculator.GetNormalizedPeakAmplitude().Should().Be((double)32768 / (1 << 15));
        }

        [Fact]
        public void GetNormalizedPeakAmplitude_WithIntMinValueSample_Should_Not_Throw()
        {
            // A 32-bit source can legitimately decode a sample of exactly int.MinValue.
            // Math.Abs(int.MinValue) throws OverflowException (confirmed by reverting the fix and
            // re-running this exact test, not assumed) since -int.MinValue doesn't fit back into
            // int32 -- the fix promotes to long before taking the absolute value.
            var calculator = new WaveformCalculator(totalSamplesPerChannel: 1, channels: 1, bitsPerSample: 32);

            var act = () => calculator.AddBlock([int.MinValue]);

            act.Should().NotThrow();
            calculator.GetNormalizedPeakAmplitude().Should().Be(1.0);
        }

        [Fact]
        public void GetNormalizedRmsLevel_BeforeAnyBlock_Should_Be_Zero_Not_NaN()
        {
            var calculator = new WaveformCalculator(totalSamplesPerChannel: 100, channels: 1, bitsPerSample: 16);

            calculator.GetNormalizedRmsLevel().Should().Be(0.0);
        }

        [Fact]
        public void GetNormalizedRmsLevel_WithSilence_Should_Be_Zero()
        {
            var calculator = new WaveformCalculator(totalSamplesPerChannel: 10, channels: 1, bitsPerSample: 16);

            calculator.AddBlock(new int[10]);

            calculator.GetNormalizedRmsLevel().Should().Be(0.0);
        }

        [Fact]
        public void GetNormalizedRmsLevel_Should_Compute_RootMeanSquare_Across_EveryIndividualSample()
        {
            // Unlike GetNormalizedPeakAmplitude (per-frame max across channels), RMS treats every
            // individual sample -- every channel of every frame -- equally.
            var calculator = new WaveformCalculator(totalSamplesPerChannel: 2, channels: 2, bitsPerSample: 16);
            int[] samples = [100, 200, 300, 400];

            calculator.AddBlock(samples);

            var maxAmplitude = 1 << 15;
            var expected = Math.Sqrt(samples.Average(s => (double)s * s)) / maxAmplitude;
            calculator.GetNormalizedRmsLevel().Should().BeApproximately(expected, 1e-12);
        }

        [Fact]
        public void AddBlock_SplitAcrossMultipleCalls_Should_Produce_Same_PeakAndRms_As_SingleCall()
        {
            var random = new Random(42);
            var samples = new int[4000];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = random.Next(short.MinValue, short.MaxValue);
            }

            var singleCallCalculator = new WaveformCalculator(totalSamplesPerChannel: 2000, channels: 2, bitsPerSample: 16);
            singleCallCalculator.AddBlock(samples);

            var multiCallCalculator = new WaveformCalculator(totalSamplesPerChannel: 2000, channels: 2, bitsPerSample: 16);
            const int framesPerChunk = 37;
            foreach (var chunk in samples.Chunk(framesPerChunk * 2))
            {
                multiCallCalculator.AddBlock(chunk);
            }

            multiCallCalculator.GetNormalizedPeakAmplitude().Should().Be(singleCallCalculator.GetNormalizedPeakAmplitude());
            multiCallCalculator.GetNormalizedRmsLevel().Should().BeApproximately(singleCallCalculator.GetNormalizedRmsLevel(), 1e-12);
        }
    }
}
