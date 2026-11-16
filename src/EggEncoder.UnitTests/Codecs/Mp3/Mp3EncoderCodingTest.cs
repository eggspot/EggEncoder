using System.Diagnostics;
using EggEncoder.Codecs.Mp3;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Mp3
{
    public class Mp3EncoderCodingTest
    {
        private const int SampleRate = 44100;
        private const int Channels = 2;
        private const int DecodedLag = 1057;

        [Theory]
        [InlineData(192, 22)]
        [InlineData(320, 31.5)]
        public void Noisy_Music_At_A_Fixed_Bitrate_Should_Meet_An_Snr_Floor(int bitRateKbps, double minimumSnrDb)
        {
            var samples = NoisyMusic(seconds: 3);

            var path = Encode(samples, encoder => Mp3Encoder.OpenSession(encoder, Channels, SampleRate, bitsPerSample: 16, bitRateKbps: bitRateKbps));

            try
            {
                Snr(samples, path).Should().BeGreaterThan(minimumSnrDb);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Hard_Content_At_The_Best_Vbr_Quality_Should_Get_A_High_Bitrate()
        {
            var samples = NoisyMusic(seconds: 3);

            var path = Encode(samples, encoder => Mp3Encoder.OpenVbrSession(encoder, Channels, SampleRate, bitsPerSample: 16, quality: 0));

            try
            {
                var kilobitsPerSecond = new FileInfo(path).Length * 8.0 / 3 / 1000;
                kilobitsPerSecond.Should().BeGreaterThan(250);
                Snr(samples, path).Should().BeGreaterThan(30);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Correlated_Stereo_Should_Use_Mid_Side_Frames_And_Gain_Snr()
        {
            var samples = Stereo(seconds: 3, correlation: 0.95);

            var path = Encode(samples, encoder => Mp3Encoder.OpenSession(encoder, Channels, SampleRate, bitsPerSample: 16, bitRateKbps: 192));

            try
            {
                MidSideFrames(File.ReadAllBytes(path)).Should().BeGreaterThan(100);
                Snr(samples, path).Should().BeGreaterThan(29);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Independent_Channels_Should_Not_Use_Mid_Side()
        {
            var samples = Stereo(seconds: 3, correlation: 0);

            var path = Encode(samples, encoder => Mp3Encoder.OpenSession(encoder, Channels, SampleRate, bitsPerSample: 16, bitRateKbps: 192));

            try
            {
                MidSideFrames(File.ReadAllBytes(path)).Should().Be(0);
                Snr(samples, path).Should().BeGreaterThan(21.5);
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static int MidSideFrames(byte[] bytes)
        {
            var count = 0;
            var offset = 0;
            while (offset + 4 <= bytes.Length)
            {
                if (bytes[offset] != 0xFF || (bytes[offset + 1] & 0xE0) != 0xE0)
                {
                    offset++;
                    continue;
                }

                var bitrateIndex = bytes[offset + 2] >> 4;
                var padding = (bytes[offset + 2] >> 1) & 1;
                var mode = bytes[offset + 3] >> 6;
                var extension = (bytes[offset + 3] >> 4) & 3;
                if (mode == 1 && (extension & 2) != 0)
                {
                    count++;
                }

                int[] rates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
                offset += (144000 * rates[bitrateIndex] / SampleRate) + padding;
            }

            return count;
        }

        private static int[] Stereo(int seconds, double correlation)
        {
            var random = new Random(31);
            var frames = seconds * SampleRate;
            var samples = new int[frames * Channels];
            for (var frame = 0; frame < frames; frame++)
            {
                var t = frame / (double)SampleRate;
                var shared = (6000 * Math.Sin(2 * Math.PI * 330 * t)) + ((random.NextDouble() - 0.5) * 3000);
                var own = (6000 * Math.Sin(2 * Math.PI * 523 * t)) + ((random.NextDouble() - 0.5) * 3000);
                samples[frame * Channels] = (int)shared;
                samples[(frame * Channels) + 1] = (int)((correlation * shared) + ((1 - correlation) * own));
            }

            return samples;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Ffmpeg_Should_Trim_The_Encoder_Delay_And_Padding(bool variableBitRate)
        {
            if (Environment.GetEnvironmentVariable("EGGENCODER_FFMPEG_ORACLE") != "1")
            {
                return;
            }

            var samples = Stereo(seconds: 2, correlation: 0.5);
            var path = Encode(samples, encoder => variableBitRate
                ? Mp3Encoder.OpenVbrSession(encoder, Channels, SampleRate, bitsPerSample: 16, quality: 2)
                : Mp3Encoder.OpenSession(encoder, Channels, SampleRate, bitsPerSample: 16, bitRateKbps: 192));
            try
            {
                using var process = Process.Start(new ProcessStartInfo("ffmpeg", ["-v", "error", "-i", path, "-f", "s16le", "-ac", "2", "-"]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
                using var pcm = new MemoryStream();
                process.StandardOutput.BaseStream.CopyTo(pcm);
                process.WaitForExit();
                process.ExitCode.Should().Be(0, process.StandardError.ReadToEnd());
                var bytes = pcm.ToArray();

                (bytes.Length / 2).Should().Be(samples.Length);
                double signal = 0;
                double noise = 0;
                for (var i = SampleRate / 10 * Channels; i < samples.Length - (SampleRate / 10 * Channels); i++)
                {
                    var actual = BitConverter.ToInt16(bytes, i * 2);
                    signal += (double)samples[i] * samples[i];
                    noise += (double)(actual - samples[i]) * (actual - samples[i]);
                }

                (10 * Math.Log10(signal / Math.Max(noise, 1))).Should().BeGreaterThan(15);
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static string Encode(int[] samples, Func<string, Mp3EncoderSession> open)
        {
            var path = Path.Combine(Path.GetTempPath(), $"mp3_coding_{Guid.NewGuid():N}.mp3");
            using var session = open(path);
            session.WriteInterleavedSamples(samples, samples.Length / Channels);
            session.Finish();
            return path;
        }

        private static double Snr(int[] samples, string path)
        {
            var (_, decoded) = Mp3TestDecoder.DecodeAll(path);
            double signal = 0;
            double noise = 0;
            var frames = samples.Length / Channels;
            for (var frame = SampleRate / 10; frame < frames - (SampleRate / 10); frame++)
            {
                for (var channel = 0; channel < Channels; channel++)
                {
                    var expected = samples[(frame * Channels) + channel];
                    var actual = decoded[((frame + DecodedLag) * Channels) + channel];
                    signal += (double)expected * expected;
                    noise += (double)(actual - expected) * (actual - expected);
                }
            }

            return 10 * Math.Log10(signal / Math.Max(noise, 1));
        }

        private static int[] NoisyMusic(int seconds)
        {
            var random = new Random(23);
            var frames = seconds * SampleRate;
            var samples = new int[frames * Channels];
            var low = 0.0;
            for (var frame = 0; frame < frames; frame++)
            {
                var t = frame / (double)SampleRate;
                low = (0.97 * low) + (0.03 * (random.NextDouble() - 0.5));
                var noise = (random.NextDouble() - 0.5) * 1500;
                var tone = (7000 * Math.Sin(2 * Math.PI * 220 * t)) + (2500 * Math.Sin(2 * Math.PI * 660 * t));
                samples[frame * Channels] = (int)(tone + noise + (60000 * low));
                samples[(frame * Channels) + 1] = (int)((0.8 * tone) + noise + (50000 * low));
            }

            return samples;
        }
    }
}
