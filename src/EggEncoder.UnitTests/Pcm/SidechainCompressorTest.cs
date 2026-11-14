using System.Diagnostics;
using EggEncoder.Codecs;
using EggEncoder.Codecs.Wav;
using EggEncoder.Pcm;
using FluentAssertions;

namespace EggEncoder.UnitTests.Pcm
{
    public class SidechainCompressorTest
    {
        private const int SampleRate = 48000;

        private static readonly SidechainCompressorSettings Renderer = new() { Threshold = 0.05, Ratio = 8, AttackMs = 20, ReleaseMs = 400 };

        public static TheoryData<string, SidechainCompressorSettings, (int Index, double Gain)[]> FfmpegGains => new()
        {
            {
                "renderer", Renderer,
                [
                    (0, 1.0), (9599, 1.0), (9700, 0.227144), (9800, 0.183461), (10000, 0.160172), (10560, 0.132376),
                    (12000, 0.122361), (14400, 0.120791), (23999, 0.120675), (24500, 0.126313), (26000, 0.144821),
                    (28800, 0.18693), (33599, 0.289509), (33700, 0.196547), (36000, 0.122239), (38000, 0.120536),
                    (40000, 0.139749), (47999, 0.289744)
                ]
            },
            {
                "ffmpeg defaults", new SidechainCompressorSettings(),
                [
                    (0, 1.0), (9599, 1.0), (9700, 0.677864), (9800, 0.600189), (10000, 0.556158), (10560, 0.499877),
                    (12000, 0.480087), (14400, 0.477667), (23999, 0.477533), (24500, 0.497895), (26000, 0.564201),
                    (28800, 0.712502), (33599, 0.962658), (33700, 0.660304), (36000, 0.480029), (38000, 0.476671),
                    (40000, 0.545807), (47999, 0.962729)
                ]
            },
            {
                "peak, maximum link, hard knee, makeup 2",
                Renderer with { Detection = SidechainDetection.Peak, Link = SidechainLink.Maximum, Knee = 1, Makeup = 2 },
                [
                    (0, 2.0), (9599, 2.0), (9700, 0.764672), (9800, 0.509717), (10000, 0.385097), (10560, 0.280655),
                    (12000, 0.249665), (14400, 0.245751), (23999, 0.245515), (24500, 0.268996), (26000, 0.353599),
                    (28800, 0.589121), (33599, 1.4131), (33700, 0.596245), (36000, 0.249393), (38000, 0.244962),
                    (40000, 0.32897), (47999, 1.414128)
                ]
            }
        };

        [Theory]
        [MemberData(nameof(FfmpegGains))]
        public void Duck_Should_Match_The_Gains_Ffmpeg_Sidechaincompress_Applies(string name, SidechainCompressorSettings settings, (int Index, double Gain)[] expected)
        {
            var output = SidechainCompressor.Duck(ConstantTarget(0.5f, SampleRate), BurstKey(), settings);

            foreach (var (index, gain) in expected)
            {
                (output.Samples[2 * index] / 0.5).Should().BeApproximately(gain, 0.005, $"{name} at sample {index}");
                output.Samples[(2 * index) + 1].Should().Be(output.Samples[2 * index]);
            }
        }

        [Fact]
        public void Process_SilentKey_Should_Leave_The_Target_Unchanged()
        {
            var compressor = new SidechainCompressor(SampleRate, 1, 2, Renderer);
            float[] target = [0.1f, -0.2f, 0.9f, -0.9f];

            compressor.Process(new float[2], target);

            target.Should().Equal(0.1f, -0.2f, 0.9f, -0.9f);
        }

        [Fact]
        public void Process_ConstantKey_Should_Smooth_The_Level_With_The_Ffmpeg_Attack_And_Release_Coefficients()
        {
            var settings = Renderer with { Detection = SidechainDetection.Peak };
            var compressor = new SidechainCompressor(SampleRate, 1, 1, settings);
            var attack = Math.Min(1, 4000.0 / (20 * SampleRate));
            var release = Math.Min(1, 4000.0 / (400 * SampleRate));

            compressor.Process(Constant(0.5f, 100), new float[100]);
            compressor.SmoothedKeyLevel.Should().BeApproximately(0.5 * (1 - Math.Pow(1 - attack, 100)), 1e-9);

            var reached = compressor.SmoothedKeyLevel;
            compressor.Process(new float[250], new float[250]);
            compressor.SmoothedKeyLevel.Should().BeApproximately(reached * Math.Pow(1 - release, 250), 1e-9);
        }

        [Fact]
        public void Process_LongLoudKey_Should_Reach_The_Steady_State_Gain_Of_The_Ratio()
        {
            var compressor = new SidechainCompressor(SampleRate, 1, 1, Renderer);
            var target = Constant(1f, SampleRate);

            compressor.Process(Constant(0.5f, SampleRate), target);

            target[^1].Should().BeApproximately((float)Math.Pow(0.5 / 0.05, (1 / 8.0) - 1), 1e-4f);
        }

        [Fact]
        public void Gain_Should_Be_Continuous_And_Falling_Across_The_Knee()
        {
            var previous = 1.0;
            var kneeStart = 0.05 / Math.Sqrt(2.82843);
            var kneeStop = 0.05 * Math.Sqrt(2.82843);
            for (var level = kneeStart * 0.9; level < kneeStop * 1.1; level += kneeStart / 200)
            {
                var gain = SteadyGain(Renderer with { Detection = SidechainDetection.Peak, AttackMs = 0.01 }, (float)level);
                gain.Should().BeLessThanOrEqualTo(previous + 1e-9);
                (previous - gain).Should().BeLessThan(0.01);
                previous = gain;
            }
        }

        [Fact]
        public void Process_KeyBelowTheKnee_Should_Not_Reduce_And_HardKnee_Should_Start_At_The_Threshold()
        {
            var soft = Renderer with { Detection = SidechainDetection.Peak, AttackMs = 0.01 };

            SteadyGain(soft, 0.02f).Should().Be(1);
            SteadyGain(soft with { Knee = 1 }, 0.049f).Should().Be(1);
            SteadyGain(soft with { Knee = 1 }, 0.1f).Should().BeApproximately(Math.Pow(2, (1 / 8.0) - 1), 1e-4);
        }

        [Fact]
        public void Link_Should_Average_Or_Take_The_Louder_Channel()
        {
            var peak = Renderer with { Detection = SidechainDetection.Peak, AttackMs = 0.01 };
            var average = new SidechainCompressor(SampleRate, 2, 1, peak);
            var maximum = new SidechainCompressor(SampleRate, 2, 1, peak with { Link = SidechainLink.Maximum });

            average.Process([0.4f, -0.2f], new float[1]);
            maximum.Process([0.4f, -0.2f], new float[1]);

            average.SmoothedKeyLevel.Should().BeApproximately(0.3, 1e-6);
            maximum.SmoothedKeyLevel.Should().BeApproximately(0.4, 1e-6);
        }

        [Fact]
        public void Rms_Should_Smooth_The_Squared_Level_And_KeyGain_Should_Scale_It()
        {
            var compressor = new SidechainCompressor(SampleRate, 1, 1, Renderer with { AttackMs = 0.01, KeyGain = 2 });

            compressor.Process([0.3f], new float[1]);

            compressor.SmoothedKeyLevel.Should().BeApproximately(0.36, 1e-6);
        }

        [Fact]
        public void Mix_Makeup_And_InputGain_Should_Scale_As_Documented()
        {
            float Output(SidechainCompressorSettings settings)
            {
                var target = Constant(1f, SampleRate);
                new SidechainCompressor(SampleRate, 1, 1, settings).Process(Constant(0.5f, SampleRate), target);
                return target[^1];
            }

            var wet = Output(Renderer);

            Output(Renderer with { Mix = 0, InputGain = 2 }).Should().Be(2f);
            Output(Renderer with { Makeup = 3 }).Should().BeApproximately(wet * 3, 1e-5f);
            Output(Renderer with { Mix = 0.5 }).Should().BeApproximately((wet + 1) / 2, 1e-5f);
        }

        [Fact]
        public void Process_InTwoBlocks_Should_Equal_Process_In_One()
        {
            var key = BurstKey().Samples;
            var whole = ConstantTarget(0.5f, SampleRate).Samples;
            var split = (float[])whole.Clone();

            new SidechainCompressor(SampleRate, 1, 2, Renderer).Process(key, whole);
            var compressor = new SidechainCompressor(SampleRate, 1, 2, Renderer);
            compressor.Process(key.AsSpan(0, 10000), split.AsSpan(0, 20000));
            compressor.Process(key.AsSpan(10000), split.AsSpan(20000));

            split.Should().Equal(whole);
        }

        [Fact]
        public void Reset_Should_Forget_The_Smoothed_Level()
        {
            var compressor = new SidechainCompressor(SampleRate, 1, 1, Renderer);
            compressor.Process(Constant(0.5f, 100), new float[100]);

            compressor.Reset();

            compressor.SmoothedKeyLevel.Should().Be(0);
        }

        [Fact]
        public void Duck_ShorterKey_Should_Keep_The_Target_Length_And_Release_After_The_Key_Ends()
        {
            var target = ConstantTarget(0.5f, SampleRate);
            var key = new PcmAudio(Constant(0.5f, SampleRate / 2), 1, SampleRate);

            var output = SidechainCompressor.Duck(target, key, Renderer);

            output.FrameCount.Should().Be(SampleRate);
            output.Samples[(2 * (SampleRate / 2)) - 2].Should().BeLessThan(0.1f);
            output.Samples[^1].Should().BeGreaterThan(output.Samples[SampleRate]);
            target.Samples[0].Should().Be(0.5f);
        }

        [Fact]
        public void Duck_LongerKey_At_Another_Rate_Should_Be_Converted_And_Cut()
        {
            var target = ConstantTarget(0.5f, 1000);
            var key = new PcmAudio(Constant(0.5f, 4 * 44100), 1, 44100);

            var output = SidechainCompressor.Duck(target, key, Renderer);

            output.FrameCount.Should().Be(1000);
            output.SampleRate.Should().Be(SampleRate);
            output.Samples[^1].Should().BeLessThan(0.2f);
        }

        [Fact]
        public void Duck_StereoKey_With_MonoTarget_Should_Work()
        {
            var target = new PcmAudio(Constant(0.5f, SampleRate), 1, SampleRate);
            var key = new PcmAudio(Constant(0.5f, 2 * SampleRate), 2, SampleRate);

            var output = SidechainCompressor.Duck(target, key, Renderer);

            output.Channels.Should().Be(1);
            output.Samples[^1].Should().BeLessThan(0.1f);
        }

        [Fact]
        public void Process_MismatchedFrames_Should_Throw()
        {
            var compressor = new SidechainCompressor(SampleRate, 2, 2, Renderer);

            compressor.Invoking(c => c.Process(new float[4], new float[6])).Should().Throw<ArgumentException>();
            compressor.Invoking(c => c.Process(new float[3], new float[6])).Should().Throw<ArgumentException>();
            compressor.Invoking(c => c.Process(new float[4], new float[3])).Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Constructor_InvalidArguments_Should_Throw()
        {
            var act = () => new SidechainCompressor(SampleRate, 1, 1, null!);
            act.Should().Throw<ArgumentNullException>();
            act = () => new SidechainCompressor(0, 1, 1, Renderer);
            act.Should().Throw<ArgumentOutOfRangeException>();
            act = () => new SidechainCompressor(SampleRate, 3, 1, Renderer);
            act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("keyChannels");
            act = () => new SidechainCompressor(SampleRate, 1, 0, Renderer);
            act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("targetChannels");
        }

        public static TheoryData<SidechainCompressorSettings, string> InvalidSettings => new()
        {
            { new() { InputGain = 0.01 }, "InputGain" },
            { new() { Threshold = 2 }, "Threshold" },
            { new() { Ratio = 0.5 }, "Ratio" },
            { new() { AttackMs = 0 }, "AttackMs" },
            { new() { ReleaseMs = 10000 }, "ReleaseMs" },
            { new() { Makeup = 0.5 }, "Makeup" },
            { new() { Knee = 9 }, "Knee" },
            { new() { KeyGain = double.NaN }, "KeyGain" },
            { new() { Mix = 1.5 }, "Mix" },
            { new() { Detection = (SidechainDetection)7 }, "Detection" },
            { new() { Link = (SidechainLink)7 }, "Link" }
        };

        [Theory]
        [MemberData(nameof(InvalidSettings))]
        public void Validate_OutOfRange_Should_Name_The_Property(SidechainCompressorSettings settings, string property)
        {
            settings.Invoking(s => s.Validate()).Should().Throw<ArgumentOutOfRangeException>().WithParameterName(property);
        }

        [Fact]
        public void Duck_NullArguments_Should_Throw()
        {
            var audio = ConstantTarget(0.5f, 10);

            FluentActions.Invoking(() => SidechainCompressor.Duck(null!, audio, Renderer)).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => SidechainCompressor.Duck(audio, null!, Renderer)).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => SidechainCompressor.Duck(audio, audio, null!)).Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Duck_Should_Stay_Close_To_Live_Ffmpeg_Over_The_Whole_Signal()
        {
            if (Environment.GetEnvironmentVariable("EGGENCODER_FFMPEG_ORACLE") != "1")
            {
                return;
            }

            var directory = Directory.CreateTempSubdirectory("eggencoder-sidechain-").FullName;
            var target = new PcmAudio(Interleave(SampleRate, i => (float)(0.4 * Math.Sin(2 * Math.PI * 440 * i / SampleRate))), 2, SampleRate);
            var key = BurstKey();
            WriteFloatWav(Path.Combine(directory, "target.wav"), target);
            WriteFloatWav(Path.Combine(directory, "key.wav"), key);
            var output = Path.Combine(directory, "ffmpeg.wav");
            Run($"-v error -y -i \"{Path.Combine(directory, "target.wav")}\" -i \"{Path.Combine(directory, "key.wav")}\" " +
                $"-filter_complex \"[0:a][1:a]sidechaincompress=threshold=0.05:ratio=8:attack=20:release=400\" -c:a pcm_f32le \"{output}\"");

            var ffmpeg = PcmAudio.Decode(output).Samples;
            var ours = SidechainCompressor.Duck(target, key, Renderer).Samples;

            ffmpeg.Length.Should().Be(ours.Length);
            var squared = 0.0;
            for (var i = 0; i < ours.Length; i++)
            {
                squared += Math.Pow(ours[i] - ffmpeg[i], 2);
            }

            Math.Sqrt(squared / ours.Length).Should().BeLessThan(0.002);
        }

        private static double SteadyGain(SidechainCompressorSettings settings, float keyLevel)
        {
            var target = Constant(1f, SampleRate / 4);
            new SidechainCompressor(SampleRate, 1, 1, settings).Process(Constant(keyLevel, SampleRate / 4), target);
            return target[^1];
        }

        private static float[] Constant(float value, int count) => Enumerable.Repeat(value, count).ToArray();

        private static PcmAudio ConstantTarget(float value, int frames) => new(Constant(value, 2 * frames), 2, SampleRate);

        private static PcmAudio BurstKey()
        {
            var samples = new float[SampleRate];
            for (var i = 0; i < samples.Length; i++)
            {
                var seconds = i / (double)SampleRate;
                var on = seconds is >= 0.2 and < 0.5 || seconds is >= 0.7 and < 0.8;
                samples[i] = on ? (float)(0.6 * Math.Sin(2 * Math.PI * 200 * i / SampleRate)) : 0f;
            }

            return new PcmAudio(samples, 1, SampleRate);
        }

        private static float[] Interleave(int frames, Func<int, float> sample)
        {
            var samples = new float[2 * frames];
            for (var i = 0; i < frames; i++)
            {
                samples[2 * i] = sample(i);
                samples[(2 * i) + 1] = sample(i);
            }

            return samples;
        }

        private static void Run(string arguments)
        {
            var startInfo = new ProcessStartInfo("ffmpeg", arguments) { RedirectStandardError = true, UseShellExecute = false };
            using var process = Process.Start(startInfo)!;
            var text = process.StandardError.ReadToEnd();
            process.WaitForExit();
            process.ExitCode.Should().Be(0, text);
        }

        private static void WriteFloatWav(string path, PcmAudio audio)
        {
            using var writer = WavWriter.Create(path, audio.Channels, audio.SampleRate, 32, audio.FrameCount, WavSampleFormat.Float32);
            writer.WriteInterleavedSamples(FloatSampleConverter.FromFloat(audio.Samples), (int)audio.FrameCount);
            writer.Finish();
        }
    }
}
