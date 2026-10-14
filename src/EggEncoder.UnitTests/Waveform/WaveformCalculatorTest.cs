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

        // -------- Integrated loudness (ITU-R BS.1770-4 / EBU R128) --------

        [Fact]
        public void GetIntegratedLoudness_WithoutSampleRate_Should_ReturnNull()
        {
            var calculator = new WaveformCalculator(totalSamplesPerChannel: 48000 * 2, channels: 2, bitsPerSample: 16);
            calculator.AddBlock(BuildSineWave(sampleRate: 48000, seconds: 2, channels: 2, bitsPerSample: 16, peakDbfs: -20, frequencyHz: 997));

            calculator.GetIntegratedLoudness().Should().BeNull();
        }

        [Fact]
        public void GetIntegratedLoudness_WithLessThan400MsOfAudio_Should_ReturnNull()
        {
            const int sampleRate = 48000;
            var calculator = new WaveformCalculator(totalSamplesPerChannel: sampleRate / 10, channels: 1, bitsPerSample: 16, sampleRate: sampleRate);

            // 100ms -- under the 400ms needed to ever form a single gating block.
            calculator.AddBlock(BuildSineWave(sampleRate, seconds: 0, channels: 1, bitsPerSample: 16, peakDbfs: -20, frequencyHz: 997, frameCountOverride: sampleRate / 10));

            calculator.GetIntegratedLoudness().Should().BeNull();
        }

        [Fact]
        public void GetIntegratedLoudness_WithPureSilence_Should_ReturnNegativeInfinity()
        {
            const int sampleRate = 48000;
            var calculator = new WaveformCalculator(totalSamplesPerChannel: sampleRate * 2, channels: 2, bitsPerSample: 16, sampleRate: sampleRate);

            calculator.AddBlock(new int[sampleRate * 2 * 2]);

            calculator.GetIntegratedLoudness().Should().Be(double.NegativeInfinity);
        }

        // The official ITU-R BS.1770-4 conformance test signal: a 997Hz sine wave at -23dBFS
        // (peak amplitude) must measure -23.0 LUFS, within the standard's own stated tolerance.
        // Confirmed against this exact target empirically before writing this test (a scratch
        // console run against this same implementation), not assumed from the K-weighting filter
        // coefficients alone -- see KWeightingFilter's own doc comment for where those coefficients
        // were independently verified against the reference libebur128 source.
        [Theory]
        [InlineData(-23.0)]
        [InlineData(-20.0)]
        [InlineData(-40.0)]
        public void GetIntegratedLoudness_WithReference997HzSineWave_Should_MatchItsOwnDbfsLevel(double peakDbfs)
        {
            const int sampleRate = 48000;
            var calculator = new WaveformCalculator(totalSamplesPerChannel: sampleRate * 2, channels: 2, bitsPerSample: 24, sampleRate: sampleRate);

            calculator.AddBlock(BuildSineWave(sampleRate, seconds: 2, channels: 2, bitsPerSample: 24, peakDbfs, frequencyHz: 997));

            var lufs = calculator.GetIntegratedLoudness();

            lufs.Should().NotBeNull();
            lufs!.Value.Should().BeApproximately(peakDbfs, 0.1);
        }

        [Fact]
        public void GetIntegratedLoudness_Should_IgnoreATrailingQuietPassage_BelowTheRelativeGate()
        {
            const int sampleRate = 48000;
            const int channels = 1;
            const int bitsPerSample = 24;

            var loudSeconds = BuildSineWave(sampleRate, seconds: 4, channels, bitsPerSample, peakDbfs: -20, frequencyHz: 997);
            // -40dBFS is 20dB (well past the 10 LU relative gate) quieter than the loud passage,
            // so it should be excluded from the integrated result almost entirely.
            var quietSeconds = BuildSineWave(sampleRate, seconds: 4, channels, bitsPerSample, peakDbfs: -40, frequencyHz: 997);

            var loudOnlyCalculator = new WaveformCalculator(totalSamplesPerChannel: sampleRate * 4, channels, bitsPerSample, sampleRate: sampleRate);
            loudOnlyCalculator.AddBlock(loudSeconds);
            var loudOnlyLufs = loudOnlyCalculator.GetIntegratedLoudness();

            var mixedCalculator = new WaveformCalculator(totalSamplesPerChannel: sampleRate * 8, channels, bitsPerSample, sampleRate: sampleRate);
            mixedCalculator.AddBlock(loudSeconds);
            mixedCalculator.AddBlock(quietSeconds);
            var mixedLufs = mixedCalculator.GetIntegratedLoudness();

            mixedLufs.Should().NotBeNull();
            mixedLufs!.Value.Should().BeApproximately(loudOnlyLufs!.Value, 0.2);
        }

        [Fact]
        public void GetIntegratedLoudness_CalledTwice_Should_Return_TheSameValue()
        {
            const int sampleRate = 48000;
            var calculator = new WaveformCalculator(totalSamplesPerChannel: sampleRate * 2, channels: 1, bitsPerSample: 16, sampleRate: sampleRate);
            calculator.AddBlock(BuildSineWave(sampleRate, seconds: 2, channels: 1, bitsPerSample: 16, peakDbfs: -20, frequencyHz: 997));

            var first = calculator.GetIntegratedLoudness();
            var second = calculator.GetIntegratedLoudness();

            second.Should().Be(first);
        }

        private static int[] BuildSineWave(int sampleRate, int seconds, int channels, int bitsPerSample, double peakDbfs, double frequencyHz, int? frameCountOverride = null)
        {
            var frameCount = frameCountOverride ?? sampleRate * seconds;
            var maxAmplitude = 1 << (bitsPerSample - 1);
            var amplitude = Math.Pow(10, peakDbfs / 20.0) * maxAmplitude;

            var interleaved = new int[frameCount * channels];
            for (var frame = 0; frame < frameCount; frame++)
            {
                var sample = (int)Math.Round(amplitude * Math.Sin(2 * Math.PI * frequencyHz * frame / sampleRate));
                for (var c = 0; c < channels; c++)
                {
                    interleaved[(frame * channels) + c] = sample;
                }
            }

            return interleaved;
        }
    }
}
