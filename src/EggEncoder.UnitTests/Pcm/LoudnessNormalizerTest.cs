using System.Diagnostics;
using System.Text.RegularExpressions;
using EggEncoder.Codecs;
using EggEncoder.Codecs.Wav;
using EggEncoder.Pcm;
using FluentAssertions;

namespace EggEncoder.UnitTests.Pcm
{
    public class LoudnessNormalizerTest
    {
        [Fact]
        public void Normalize_WithoutLimiting_Should_Reach_The_Target()
        {
            var quiet = Scaled(LoudnessMeterTest.ToneAndNoise(48000), 0.1f);

            var result = LoudnessNormalizer.Normalize(quiet, -20);

            LoudnessMeter.MeasureIntegratedLoudness(result)!.Value.Should().BeApproximately(-20, 0.5);
            LoudnessMeter.MeasureTruePeakDb(result).Should().BeLessThanOrEqualTo(LoudnessNormalizer.DefaultTruePeakCeilingDb);
        }

        [Fact]
        public void Normalize_Should_Leave_The_Input_Unchanged()
        {
            var input = LoudnessMeterTest.ToneAndNoise(48000);
            var before = (float[])input.Samples.Clone();

            LoudnessNormalizer.Normalize(input, -14);

            input.Samples.Should().Equal(before);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void Normalize_LoudSignals_Should_Never_Pass_The_Ceiling(int signal)
        {
            var audio = signal switch
            {
                0 => SquareWave(),
                1 => NearNyquistBursts(),
                2 => RandomNoise(),
                _ => LoudnessMeterTest.IntersamplePeak()
            };

            var result = LoudnessNormalizer.Normalize(audio, -5, -1.5);

            LoudnessMeter.MeasureTruePeakDb(result).Should().BeLessThanOrEqualTo(-1.5);
        }

        [Fact]
        public void Normalize_ShortLoudSpikes_Should_Be_Limited_Not_Turned_Down_With_The_Whole_Signal()
        {
            var samples = new float[48000 * 3];
            for (var n = 0; n < samples.Length; n++)
            {
                var spike = n % 24000 < 240 ? 0.9 : 0.1;
                samples[n] = (float)(spike * Math.Sin(2 * Math.PI * 1000 * n / 48000));
            }

            var audio = new PcmAudio(samples, 1, 48000);
            var gain = Math.Pow(10, (-14 - LoudnessMeter.MeasureIntegratedLoudness(audio)!.Value) / 20);
            var peakAfterGain = LoudnessMeter.MeasureTruePeakDb(audio) + (20 * Math.Log10(gain));
            var constantGainLoudness = -14 + Math.Min(0, -1.5 - peakAfterGain);

            var result = LoudnessNormalizer.Normalize(audio, -14, -1.5);

            LoudnessMeter.MeasureTruePeakDb(result).Should().BeLessThanOrEqualTo(-1.5);
            LoudnessMeter.MeasureIntegratedLoudness(result)!.Value.Should().BeGreaterThan(constantGainLoudness + 3).And.BeLessThanOrEqualTo(-13.9);
        }

        [Fact]
        public void Normalize_TooShortToMeasure_Should_Only_Limit()
        {
            var shortLoud = PcmAudioTest.Tone(48000, 1, 1000, 1.5, 4800);

            var result = LoudnessNormalizer.Normalize(shortLoud, -30, -3);

            LoudnessMeter.MeasureTruePeakDb(result).Should().BeLessThanOrEqualTo(-3);
            result.Samples.Max().Should().BeGreaterThan(0.6f);
        }

        [Fact]
        public void Normalize_SilenceWithAClick_Should_Limit_The_Click_Without_Gain()
        {
            var samples = new float[48000];
            samples[24000] = 0.0009f;
            var audio = new PcmAudio(samples, 1, 48000);

            var result = LoudnessNormalizer.Normalize(audio, -14, -70);

            LoudnessMeter.MeasureTruePeakDb(result).Should().BeLessThanOrEqualTo(-70);
        }

        [Fact]
        public void Normalize_Empty_Should_Return_Empty()
        {
            LoudnessNormalizer.Normalize(new PcmAudio([], 2, 48000), -14).FrameCount.Should().Be(0);
        }

        [Theory]
        [InlineData(double.NaN, -1.5)]
        [InlineData(-71, -1.5)]
        [InlineData(1, -1.5)]
        [InlineData(-14, 0.5)]
        [InlineData(-14, -71)]
        [InlineData(-14, double.NegativeInfinity)]
        public void Normalize_OutOfRangeArguments_Should_Throw(double target, double ceiling)
        {
            var act = () => LoudnessNormalizer.Normalize(new PcmAudio([0f], 1, 48000), target, ceiling);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Normalize_NullAudio_Should_Throw_ArgumentNull()
        {
            var act = () => LoudnessNormalizer.Normalize(null!, -14);

            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Normalize_Should_Land_Near_Ffmpeg_Two_Pass_Linear_Loudnorm()
        {
            if (Environment.GetEnvironmentVariable("EGGENCODER_FFMPEG_ORACLE") != "1")
            {
                return;
            }

            var input = Scaled(LoudnessMeterTest.ToneAndNoise(48000), 0.3f);
            var source = Path.Combine(Path.GetTempPath(), $"eggencoder-loudnorm-{Guid.NewGuid():N}.wav");
            var output = Path.Combine(Path.GetTempPath(), $"eggencoder-loudnorm-{Guid.NewGuid():N}.wav");
            try
            {
                WriteFloatWav(source, input);
                var first = Run($"-hide_banner -nostats -i \"{source}\" -af loudnorm=I=-16:TP=-1.5:print_format=json -f null -");
                string Measured(string name) => Regex.Match(first, $"\"{name}\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
                var filter = $"loudnorm=I=-16:TP=-1.5:linear=true:measured_I={Measured("input_i")}:measured_TP={Measured("input_tp")}:" +
                    $"measured_LRA={Measured("input_lra")}:measured_thresh={Measured("input_thresh")}:offset={Measured("target_offset")}";
                Run($"-v error -y -i \"{source}\" -af {filter} -ar 48000 -c:a pcm_f32le \"{output}\"");

                var ffmpegLoudness = LoudnessMeter.MeasureIntegratedLoudness(PcmAudio.Decode(output))!.Value;
                var ours = LoudnessMeter.MeasureIntegratedLoudness(LoudnessNormalizer.Normalize(input, -16, -1.5))!.Value;

                ours.Should().BeApproximately(ffmpegLoudness, 1.0);
            }
            finally
            {
                File.Delete(source);
                File.Delete(output);
            }
        }

        private static string Run(string arguments)
        {
            var startInfo = new ProcessStartInfo("ffmpeg", arguments) { RedirectStandardError = true, UseShellExecute = false };
            using var process = Process.Start(startInfo)!;
            var text = process.StandardError.ReadToEnd();
            process.WaitForExit();
            process.ExitCode.Should().Be(0, text);
            return text;
        }

        private static void WriteFloatWav(string path, PcmAudio audio)
        {
            using var writer = WavWriter.Create(path, audio.Channels, audio.SampleRate, 32, audio.FrameCount, WavSampleFormat.Float32);
            var samples = FloatSampleConverter.FromFloat(audio.Samples);
            writer.WriteInterleavedSamples(samples, (int)audio.FrameCount);
            writer.Finish();
        }

        private static PcmAudio Scaled(PcmAudio audio, float gain) =>
            new(audio.Samples.Select(s => s * gain).ToArray(), audio.Channels, audio.SampleRate);

        private static PcmAudio SquareWave()
        {
            var samples = new float[48000];
            for (var n = 0; n < samples.Length; n++)
            {
                samples[n] = (n / 24 % 2 == 0) ? 0.9f : -0.9f;
            }

            return new PcmAudio(samples, 1, 48000);
        }

        private static PcmAudio NearNyquistBursts()
        {
            var samples = new float[96000];
            for (var n = 0; n < 48000; n++)
            {
                var on = n / 2400 % 2 == 0 ? 1.0 : 0.05;
                samples[2 * n] = (float)(0.95 * on * Math.Sin((2 * Math.PI * 21000 * n / 48000) + 0.3));
                samples[(2 * n) + 1] = (float)(0.95 * on * Math.Sin((2 * Math.PI * 23000 * n / 48000) + 1.1));
            }

            return new PcmAudio(samples, 2, 48000);
        }

        private static PcmAudio RandomNoise()
        {
            var random = new Random(3);
            var samples = new float[96000];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (float)((random.NextDouble() * 2) - 1);
            }

            return new PcmAudio(samples, 2, 48000);
        }
    }
}
