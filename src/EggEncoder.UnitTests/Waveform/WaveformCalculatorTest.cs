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
    }
}
