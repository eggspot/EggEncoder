using EggEncoder.Pcm;
using FluentAssertions;

namespace EggEncoder.UnitTests.Pcm
{
    public class LoudnessMeterTest
    {
        [Theory]
        [InlineData(48000, -12.0)]
        [InlineData(44100, -11.9)]
        public void MeasureIntegratedLoudness_Should_Match_Ffmpeg_Ebur128(int sampleRate, double ffmpegLufs)
        {
            var loudness = LoudnessMeter.MeasureIntegratedLoudness(ToneAndNoise(sampleRate));

            loudness.Should().NotBeNull();
            loudness!.Value.Should().BeApproximately(ffmpegLufs, 0.15);
        }

        [Fact]
        public void MeasureTruePeakDb_Should_Find_The_Peak_Between_Samples_Like_Ffmpeg()
        {
            var audio = IntersamplePeak();

            LoudnessMeter.MeasureTruePeakDb(audio).Should().BeApproximately(-0.9, 0.1);
            var samplePeak = audio.Samples.Max(Math.Abs);
            (20 * Math.Log10(samplePeak)).Should().BeApproximately(-3.9, 0.1);
        }

        [Fact]
        public void MeasureTruePeakDb_ToneAndNoise_Should_Match_Ffmpeg()
        {
            LoudnessMeter.MeasureTruePeakDb(ToneAndNoise(48000)).Should().BeApproximately(-10.5, 0.1);
        }

        [Fact]
        public void MeasureTruePeakDb_Silence_Should_Be_Negative_Infinity()
        {
            LoudnessMeter.MeasureTruePeakDb(PcmAudio.Silence(2, 48000, TimeSpan.FromSeconds(1))).Should().Be(double.NegativeInfinity);
            LoudnessMeter.MeasureTruePeakDb(new PcmAudio([], 1, 48000)).Should().Be(double.NegativeInfinity);
        }

        [Fact]
        public void MeasureIntegratedLoudness_Silence_Should_Be_Negative_Infinity()
        {
            LoudnessMeter.MeasureIntegratedLoudness(PcmAudio.Silence(1, 48000, TimeSpan.FromSeconds(1))).Should().Be(double.NegativeInfinity);
        }

        [Fact]
        public void MeasureIntegratedLoudness_ShorterThan400Ms_Should_Be_Null()
        {
            var audio = PcmAudioTest.Tone(48000, 1, 1000, 0.5, 19199);

            LoudnessMeter.MeasureIntegratedLoudness(audio).Should().BeNull();
        }

        [Fact]
        public void MeasureIntegratedLoudness_Exactly400Ms_Should_Measure()
        {
            var audio = PcmAudioTest.Tone(48000, 1, 1000, 0.5, 19200);

            LoudnessMeter.MeasureIntegratedLoudness(audio).Should().NotBeNull();
        }

        [Fact]
        public void Measure_NonFiniteSample_Should_Throw_Argument()
        {
            var audio = new PcmAudio([0f, float.NaN], 1, 48000);

            ((Action)(() => LoudnessMeter.MeasureIntegratedLoudness(audio))).Should().Throw<ArgumentException>();
            ((Action)(() => LoudnessMeter.MeasureTruePeakDb(audio))).Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Measure_NullAudio_Should_Throw_ArgumentNull()
        {
            ((Action)(() => LoudnessMeter.MeasureIntegratedLoudness(null!))).Should().Throw<ArgumentNullException>();
            ((Action)(() => LoudnessMeter.MeasureTruePeakDb(null!))).Should().Throw<ArgumentNullException>();
        }

        internal static PcmAudio ToneAndNoise(int sampleRate)
        {
            var frames = sampleRate * 3;
            var samples = new float[frames * 2];
            long state = 12345;
            for (var n = 0; n < frames; n++)
            {
                state = ((state * 1103515245) + 12345) % 2147483648;
                var noise = ((state / 2147483648.0 * 2) - 1) * 0.05;
                samples[2 * n] = (float)(0.3 * Math.Sin(2 * Math.PI * 1000 * n / sampleRate));
                samples[(2 * n) + 1] = (float)((0.2 * Math.Sin(2 * Math.PI * 440 * n / sampleRate)) + noise);
            }

            return new PcmAudio(samples, 2, sampleRate);
        }

        internal static PcmAudio IntersamplePeak()
        {
            const int frames = 48000;
            const int fade = 480;
            var samples = new float[frames * 2];
            for (var n = 0; n < frames; n++)
            {
                var envelope = n < fade ? 0.5 - (0.5 * Math.Cos(Math.PI * n / fade))
                    : n >= frames - fade ? 0.5 - (0.5 * Math.Cos(Math.PI * (frames - 1 - n) / fade))
                    : 1.0;
                samples[2 * n] = (float)(0.9 * envelope * Math.Sin((2 * Math.PI * 12000 * n / 48000) + (Math.PI / 4)));
                samples[(2 * n) + 1] = (float)(0.5 * envelope * Math.Sin(2 * Math.PI * 1000 * n / 48000));
            }

            return new PcmAudio(samples, 2, 48000);
        }
    }
}
