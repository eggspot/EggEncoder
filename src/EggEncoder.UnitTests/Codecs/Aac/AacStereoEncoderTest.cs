using EggEncoder.Codecs.Aac;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Aac
{
    public class AacStereoEncoderTest
    {
        private const int SampleRate = 48000;
        private const int Delay = 1024;

        public static readonly string EncodedFixture = Path.GetFullPath("Codecs/Aac/encoded_stereo_48000_192k.aac");
        public static readonly string EncodedFixtureFfmpegDecode = Path.GetFullPath("Codecs/Aac/encoded_stereo_48000_192k_ffmpeg.pcm");

        [Fact]
        public void Encode_Stereo192k_Should_Hit_The_Bit_Rate_And_Decode_With_A_High_Snr()
        {
            var input = Signal(SampleRate * 3);
            var frames = new List<byte[]>();

            using (var encoder = new AacFrameEncoder(2, SampleRate, (payload, _, _) => frames.Add(payload), 192_000))
            {
                foreach (var sample in input)
                {
                    encoder.WriteSample(sample);
                }

                encoder.Flush();
            }

            var bits = frames.Sum(f => f.Length * 8L);
            var seconds = frames.Count * 1024.0 / SampleRate;
            (bits / seconds).Should().BeInRange(192_000 * 0.9, 192_000 * 1.05);
            frames.Should().OnlyContain(f => f.Length * 8 <= 6144 * 2);

            var decoded = Decode(frames, 2);
            Snr(input, decoded, channel: 0).Should().BeGreaterThan(25);
            Snr(input, decoded, channel: 1).Should().BeGreaterThan(25);
        }

        [Theory]
        [InlineData(1, 44100, 64_000)]
        [InlineData(1, 48000, 128_000)]
        [InlineData(2, 44100, 96_000)]
        [InlineData(2, 32000, 128_000)]
        public void Encode_AtOtherRates_Should_Stay_Within_The_Bit_Rate(int channels, int sampleRate, int bitRate)
        {
            var input = Signal(sampleRate * 2, channels, sampleRate);
            var bytes = 0L;
            var frameCount = 0;
            using (var encoder = new AacFrameEncoder(channels, sampleRate, (payload, _, _) => { bytes += payload.Length; frameCount++; }, bitRate))
            {
                foreach (var sample in input)
                {
                    encoder.WriteSample(sample);
                }

                encoder.Flush();
            }

            (bytes * 8.0 / (frameCount * 1024.0 / sampleRate)).Should().BeLessThanOrEqualTo(bitRate * 1.05);
        }

        [Fact]
        public void Encode_IdenticalChannels_Should_Decode_To_Identical_Channels()
        {
            var mono = Signal(SampleRate, channels: 1);
            var input = mono.SelectMany(s => new[] { s, s }).ToArray();
            var frames = new List<byte[]>();
            using (var encoder = new AacFrameEncoder(2, SampleRate, (payload, _, _) => frames.Add(payload), 192_000))
            {
                foreach (var sample in input)
                {
                    encoder.WriteSample(sample);
                }

                encoder.Flush();
            }

            var decoded = Decode(frames, 2);
            decoded.Where((_, i) => i % 2 == 0).Should().Equal(decoded.Where((_, i) => i % 2 == 1));
        }

        [Fact]
        public void Encode_Should_Keep_The_Signal_Polarity()
        {
            var input = Signal(SampleRate, channels: 1);
            var frames = new List<byte[]>();
            using (var encoder = new AacFrameEncoder(1, SampleRate, (payload, _, _) => frames.Add(payload), 128_000))
            {
                foreach (var sample in input)
                {
                    encoder.WriteSample(sample);
                }

                encoder.Flush();
            }

            var decoded = Decode(frames, 1);
            var correlation = 0.0;
            for (var i = 0; i + Delay < decoded.Count && i < input.Length; i++)
            {
                correlation += input[i] * (double)decoded[i + Delay];
            }

            correlation.Should().BePositive();
        }

        [Fact]
        public void Encode_FixtureSignal_Should_Reproduce_The_Checked_In_File_That_Ffmpeg_Decodes_Like_Our_Decoder()
        {
            var output = new MemoryStream();
            using (var session = AacEncoderSession.OpenSession(output, 2, SampleRate, 16, 192_000))
            {
                var input = Signal(SampleRate / 4).Select(s => (int)s).ToArray();
                session.WriteInterleavedSamples(input, input.Length / 2);
                session.Finish();
            }

            output.ToArray().Should().Equal(File.ReadAllBytes(EncodedFixture));
            var ours = new List<int>();
            AacDecoder.Decode(EncodedFixture, (block, _, _, _, _) => ours.AddRange(block));
            var ffmpegBytes = File.ReadAllBytes(EncodedFixtureFfmpegDecode);
            var ffmpeg = new short[ffmpegBytes.Length / 2];
            Buffer.BlockCopy(ffmpegBytes, 0, ffmpeg, 0, ffmpegBytes.Length);
            ffmpeg.Length.Should().Be(ours.Count);
            var signal = ffmpeg.Sum(s => (double)s * s);
            var difference = ours.Zip(ffmpeg).Sum(pair => (double)(pair.First - pair.Second) * (pair.First - pair.Second));
            (10 * Math.Log10(signal / difference)).Should().BeGreaterThan(20, "the two decoders differ only in the random noise of substituted bands");
        }

        [Fact]
        public void Constructor_NegativeBitRate_Should_Throw()
        {
            var act = () => new AacFrameEncoder(2, SampleRate, (_, _, _) => { }, -1);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Theory]
        [InlineData(192_000, 48000, 20_000)]
        [InlineData(96_000, 48000, 17_000)]
        [InlineData(64_000, 48000, 14_000)]
        [InlineData(48_000, 48000, 12_000)]
        [InlineData(32_000, 48000, 10_000)]
        [InlineData(16_000, 48000, 7_000)]
        [InlineData(96_000, 16000, 8_000)]
        public void Bandwidth_Should_Follow_The_Rate_Per_Channel(int bitRatePerChannel, int sampleRate, int expected)
        {
            AacFrameEncoder.Bandwidth(bitRatePerChannel, sampleRate).Should().Be(expected);
        }

        internal static short[] Signal(int frames, int channels = 2, int sampleRate = SampleRate)
        {
            var random = new Random(7);
            var samples = new short[frames * channels];
            for (var n = 0; n < frames; n++)
            {
                var t = n / (double)sampleRate;
                var burst = (n / (sampleRate / 8)) % 2 == 0 ? 1.0 : 0.3;
                for (var c = 0; c < channels; c++)
                {
                    var value = (6000 * Math.Sin(2 * Math.PI * (440 + (110 * c)) * t))
                        + (2500 * burst * Math.Sin(2 * Math.PI * 1760 * t))
                        + (900 * Math.Sin(2 * Math.PI * (5000 + (300 * c)) * t))
                        + (300 * ((random.NextDouble() * 2) - 1));
                    samples[(n * channels) + c] = (short)Math.Round(value);
                }
            }

            return samples;
        }

        private static List<int> Decode(List<byte[]> frames, int channels)
        {
            var decoder = new AacFrameDecoder(SampleRate, channels);
            var output = new List<int>();
            foreach (var frame in frames)
            {
                output.AddRange(decoder.DecodeFrame(new EggEncoder.Transform.BitReader(frame)));
            }

            return output;
        }

        private static double Snr(short[] input, List<int> decoded, int channel)
        {
            double signal = 0, noise = 0;
            var frames = input.Length / 2;
            for (var n = 2048; n < frames - 2048; n++)
            {
                var original = input[(n * 2) + channel];
                var error = original - decoded[((n + Delay) * 2) + channel];
                signal += original * (double)original;
                noise += error * (double)error;
            }

            return 10 * Math.Log10(signal / Math.Max(noise, 1e-9));
        }
    }
}
