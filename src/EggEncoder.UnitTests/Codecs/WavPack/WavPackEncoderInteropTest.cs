using System.Diagnostics;
using EggEncoder.Codecs.WavPack;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.WavPack
{
    public class WavPackEncoderInteropTest
    {
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void Every_Block_Should_Carry_Decorrelation_Weights_And_Samples_For_Its_Terms(int channels)
        {
            var path = Encode(Generate("music", channels, 16, 44100), channels, 16);
            try
            {
                var bytes = File.ReadAllBytes(path);
                var offset = 0;
                var blocks = 0;
                while (offset + 32 <= bytes.Length)
                {
                    var size = BitConverter.ToInt32(bytes, offset + 4);
                    var ids = SubBlocks(bytes, offset + 32, offset + 8 + size);
                    var terms = ids[0x02];
                    ids.Should().ContainKey(0x03);
                    ids[0x03].Should().Be(terms * channels);
                    ids.Should().ContainKey(0x04);
                    ids[0x04].Should().BeGreaterThan(0);
                    offset += 8 + size;
                    blocks++;
                }

                blocks.Should().BeGreaterThan(0);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Theory]
        [InlineData("silence", 1, 16)]
        [InlineData("music", 1, 16)]
        [InlineData("noise", 2, 16)]
        [InlineData("music", 2, 24)]
        [InlineData("ramp", 1, 24)]
        public void Ffmpeg_Should_Decode_The_Output_Bit_For_Bit(string content, int channels, int bits)
        {
            if (Environment.GetEnvironmentVariable("EGGENCODER_FFMPEG_ORACLE") != "1")
            {
                return;
            }

            var samples = Generate(content, channels, bits, 44100 * 2);
            var path = Encode(samples, channels, bits);
            try
            {
                var info = new ProcessStartInfo("ffmpeg", $"-v error -i \"{path}\" -f {(bits == 16 ? "s16le" : "s24le")} -")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var process = Process.Start(info)!;
                using var decoded = new MemoryStream();
                process.StandardOutput.BaseStream.CopyTo(decoded);
                var errors = process.StandardError.ReadToEnd();
                process.WaitForExit();

                errors.Should().BeEmpty();
                decoded.ToArray().Should().Equal(ToPcm(samples, bits));
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static string Encode(int[] samples, int channels, int bits)
        {
            var path = Path.Combine(Path.GetTempPath(), $"wv_interop_{Guid.NewGuid():N}.wv");
            var frames = samples.Length / channels;
            using var session = WavPackEncoderSession.OpenSession(path, channels, bits, 44100, frames);
            session.WriteInterleavedSamples(samples, frames);
            session.Finish();
            return path;
        }

        private static Dictionary<int, int> SubBlocks(byte[] bytes, int start, int end)
        {
            var ids = new Dictionary<int, int>();
            var offset = start;
            while (offset < end)
            {
                var id = bytes[offset];
                int length;
                int header;
                if ((id & 0x80) != 0)
                {
                    length = (bytes[offset + 1] | (bytes[offset + 2] << 8) | (bytes[offset + 3] << 16)) * 2;
                    header = 4;
                }
                else
                {
                    length = bytes[offset + 1] * 2;
                    header = 2;
                }

                ids[id & 0x3F] = length - ((id & 0x40) != 0 ? 1 : 0);
                offset += header + length;
            }

            return ids;
        }

        private static byte[] ToPcm(int[] samples, int bits)
        {
            var bytes = bits / 8;
            var pcm = new byte[samples.Length * bytes];
            for (var i = 0; i < samples.Length; i++)
            {
                for (var b = 0; b < bytes; b++)
                {
                    pcm[(i * bytes) + b] = (byte)(samples[i] >> (8 * b));
                }
            }

            return pcm;
        }

        private static int[] Generate(string content, int channels, int bits, int frames)
        {
            var random = new Random(1);
            var max = (1 << (bits - 1)) - 1;
            var samples = new int[frames * channels];
            for (var frame = 0; frame < frames; frame++)
            {
                for (var channel = 0; channel < channels; channel++)
                {
                    var t = frame / 44100.0;
                    var value = content switch
                    {
                        "silence" => 0,
                        "noise" => ((random.NextDouble() * 2) - 1) * max,
                        "music" => ((0.4 * Math.Sin(2 * Math.PI * (220 + (110 * channel)) * t)) + (0.2 * Math.Sin(2 * Math.PI * 1760 * t) * Math.Sin(2 * Math.PI * 3 * t)) + (0.02 * (random.NextDouble() - 0.5))) * max,
                        _ => (((frame % 1000) / 1000.0 * 2) - 1) * max * 0.9
                    };
                    samples[(frame * channels) + channel] = (int)Math.Clamp(value, -max - 1, max);
                }
            }

            return samples;
        }
    }
}
