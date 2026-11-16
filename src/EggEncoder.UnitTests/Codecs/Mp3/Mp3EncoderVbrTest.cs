using EggEncoder.Codecs.Mp3;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Mp3
{
    public class Mp3EncoderVbrTest
    {
        private const int SampleRate = 44100;
        private const int Channels = 2;
        private const int DecodedLag = 1057;
        private static readonly int[] BitratesKbps = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];

        [Fact]
        public void Silence_Then_Music_Should_Vary_The_Bitrate_And_Keep_Silence_At_32_Kbps()
        {
            var samples = SilenceThenTones(silenceSeconds: 1, soundSeconds: 2);

            var bytes = Encode(samples, Mp3Encoder.DefaultVbrQuality);
            var frames = FrameBitrates(bytes);

            frames.Distinct().Count().Should().BeGreaterThan(1);
            frames.Skip(1).Take(30).Should().OnlyContain(rate => rate == 32);
            frames.Max().Should().BeGreaterThan(32);
        }

        [Fact]
        public void The_First_Frame_Should_Be_A_Xing_Header_With_Gapless_Values()
        {
            var samples = SilenceThenTones(silenceSeconds: 0, soundSeconds: 2);
            var path = Path.Combine(Path.GetTempPath(), $"mp3_vbr_{Guid.NewGuid():N}.mp3");
            try
            {
                File.WriteAllBytes(path, Encode(samples, 2));
                var bytes = File.ReadAllBytes(path);

                System.Text.Encoding.ASCII.GetString(bytes, 4 + 32, 4).Should().Be("Xing");
                var probe = Mp3Probe.Probe(path);
                probe.EncoderDelaySamples.Should().Be(Mp3Encoder.EncoderDelaySamples);
                probe.DurationSeconds.Should().BeApproximately(2.0, 0.001);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Better_Quality_Should_Spend_More_Bits()
        {
            var samples = SilenceThenTones(silenceSeconds: 0, soundSeconds: 2);

            var best = Encode(samples, 0).Length;
            var middle = Encode(samples, 4).Length;
            var smallest = Encode(samples, 9).Length;

            best.Should().BeGreaterThan(middle);
            middle.Should().BeGreaterThan(smallest);
        }

        [Theory]
        [InlineData(0, 20)]
        [InlineData(4, 15)]
        [InlineData(9, 8)]
        public void Decoded_Audio_Should_Meet_An_Snr_Floor_For_Its_Quality(int quality, double minimumSnrDb)
        {
            var samples = SilenceThenTones(silenceSeconds: 0, soundSeconds: 2);
            var path = Path.Combine(Path.GetTempPath(), $"mp3_vbr_{Guid.NewGuid():N}.mp3");
            try
            {
                File.WriteAllBytes(path, Encode(samples, quality));
                var (_, decoded) = Mp3TestDecoder.DecodeAll(path);

                for (var channel = 0; channel < Channels; channel++)
                {
                    double signal = 0;
                    double noise = 0;
                    for (var frame = SampleRate / 10; frame < (SampleRate * 2) - (SampleRate / 10); frame++)
                    {
                        var expected = samples[(frame * Channels) + channel];
                        var actual = decoded[((frame + DecodedLag) * Channels) + channel];
                        signal += (double)expected * expected;
                        noise += (double)(actual - expected) * (actual - expected);
                    }

                    (10 * Math.Log10(signal / Math.Max(noise, 1))).Should().BeGreaterThan(minimumSnrDb, $"quality {quality}, channel {channel}");
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Quality_Outside_Zero_To_Nine_Should_Be_Refused()
        {
            var path = Path.Combine(Path.GetTempPath(), $"mp3_vbr_{Guid.NewGuid():N}.mp3");
            var act = () => Mp3Encoder.OpenVbrSession(path, 2, SampleRate, 16, quality: 10);

            act.Should().Throw<ArgumentOutOfRangeException>();
            File.Exists(path).Should().BeFalse();
        }

        private static byte[] Encode(int[] samples, int quality)
        {
            var path = Path.Combine(Path.GetTempPath(), $"mp3_vbr_{Guid.NewGuid():N}.mp3");
            try
            {
                using (var session = Mp3Encoder.OpenVbrSession(path, Channels, SampleRate, bitsPerSample: 16, quality))
                {
                    session.WriteInterleavedSamples(samples, samples.Length / Channels);
                    session.Finish();
                }

                return File.ReadAllBytes(path);
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static List<int> FrameBitrates(byte[] bytes)
        {
            var rates = new List<int>();
            var offset = 0;
            while (offset + 4 <= bytes.Length && bytes[offset] == 0xFF && (bytes[offset + 1] & 0xE0) == 0xE0)
            {
                var kbps = BitratesKbps[bytes[offset + 2] >> 4];
                var padding = (bytes[offset + 2] >> 1) & 1;
                rates.Add(kbps);
                offset += (144000 * kbps / SampleRate) + padding;
            }

            return rates;
        }

        private static int[] SilenceThenTones(int silenceSeconds, int soundSeconds)
        {
            var random = new Random(9);
            var total = (silenceSeconds + soundSeconds) * SampleRate;
            var samples = new int[total * Channels];
            for (var frame = silenceSeconds * SampleRate; frame < total; frame++)
            {
                var t = frame / (double)SampleRate;
                var left = (6000 * Math.Sin(2 * Math.PI * 440 * t)) + (3000 * Math.Sin(2 * Math.PI * 1320 * t)) + (500 * (random.NextDouble() - 0.5));
                var right = (6000 * Math.Sin(2 * Math.PI * 554 * t)) + (2500 * Math.Sin(2 * Math.PI * 2210 * t)) + (500 * (random.NextDouble() - 0.5));
                samples[frame * Channels] = (int)left;
                samples[(frame * Channels) + 1] = (int)right;
            }

            return samples;
        }
    }
}
