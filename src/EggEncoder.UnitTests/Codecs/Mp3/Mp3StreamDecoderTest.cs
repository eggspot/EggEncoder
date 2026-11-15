using System.Diagnostics;
using EggEncoder.Codecs.Mp3;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Mp3
{
    public class Mp3StreamDecoderTest
    {
        private const int FfmpegTrimmedStart = 576 + 529;

        public static TheoryData<string, int, int, long, int, int[]> Fixtures => new()
        {
            { "lsf_22050_stereo.mp3", 2, 22050, 25 * 576, FfmpegTrimmedStart, [-1002, -1227, 561, 564, 2116, 2335, 3640, 4022, 5110, 5569, 6498, 6918, 7784, 8021, 8948, 8842, 9971, 9351, 10840, 9531, 11539, 9374, 12057, 8887, 12386, 8085, 12520, 6998, 12457, 5665, 12197, 4133, 11749, 2459, 11117, 700, 10311, -1085, 9342, -2832, 8225, -4479, 6982, -5964, 5628, -7238, 4184, -8256] },
            { "mpeg25_8000_mono.mp3", 1, 8000, 14 * 576, FfmpegTrimmedStart, [15515, 15089, 13828, 11802, 9127, 5946, 2437, -1207, -4785, -8100, -10969, -13232, -14765, -15484, -15347, -14363, -12587, -10115, -7083, -3660, -36, 3591, 7020, 10061, 12546, 14340, 15342, 15497, 14797, 13277, 11025, 8163, 4851, 1271, -2378, -5896, -9088, -11777, -13814, -15087, -15525, -15105, -13848, -11824, -9148, -5965, -2452, 1196] },
            { "mpeg25_11025_stereo.mp3", 2, 11025, 14 * 576, FfmpegTrimmedStart, [-8207, 11701, -10830, 12164, -12583, 10712, -13317, 7586, -12973, 3251, -11577, -1588, -9243, -6177, -6168, -9804, -2598, -11889, 1170, -12099, 4838, -10413, 8106, -7078, 10708, -2633, 12435, 2230, 13146, 6756, 12787, 10205, 11397, 12074, 9096, 12049, 6070, 10156, 2563, 6640, -1134, 2073, -4718, -2784, -7904, -7229, -10441, -10536] },
            { "mono_44100.mp3", 1, 44100, 17 * 1152, FfmpegTrimmedStart, [-14724, -13857, -12709, -11304, -9670, -7841, -5853, -3746, -1563, 653, 2855, 5000, 7043, 8944, 10664, 12166, 13422, 14405, 15098, 15484, 15557, 15315, 14762, 13911, 12777, 11384, 9760, 7938, 5956, 3851, 1669, -547, -2751, -4899, -6947, -8854, -10580, -12093, -13360, -14356, -15063, -15465, -15553, -15327, -14790, -13953, -12833, -11453] },
            { "transients_44100.mp3", 2, 44100, 21 * 1152, FfmpegTrimmedStart, [-184, 158, -281, 184, -1897, 209, -1215, 234, 2680, 259, 5038, 283, 3738, 306, 1183, 329, -680, 351, -1, 373, 3109, 394, 3187, 414, -1821, 434, -4828, 452, -1612, 470, 2368, 487, 2063, 504, -634, 519, -1839, 533, -605, 546, 1068, 559, 808, 570, -891, 580, -821, 589] },
            { "cbr128.mp3", 2, 44100, 384 * 1152, FfmpegTrimmedStart, [-118, -118, 55, 55, 227, 227, 398, 398, 568, 568, 736, 736, 901, 901, 1062, 1062, 1218, 1218, 1371, 1371, 1518, 1518, 1659, 1659, 1793, 1793, 1920, 1920, 2040, 2040, 2152, 2152, 2255, 2255, 2349, 2349, 2434, 2434, 2510, 2510, 2576, 2576, 2632, 2632, 2677, 2677, 2712, 2712] },
            { "vbr_q4.mp3", 2, 44100, 384 * 1152, FfmpegTrimmedStart, [-134, -134, 51, 51, 235, 235, 419, 419, 602, 602, 782, 782, 959, 959, 1133, 1133, 1302, 1302, 1466, 1466, 1625, 1625, 1777, 1777, 1922, 1922, 2060, 2060, 2190, 2190, 2310, 2310, 2422, 2422, 2525, 2525, 2617, 2617, 2699, 2699, 2770, 2770, 2831, 2831, 2880, 2880, 2917, 2917] },
            { "tone.mp3", 2, 44100, 116 * 1152, FfmpegTrimmedStart, [-120, -120, 56, 56, 231, 231, 407, 407, 579, 579, 751, 751, 920, 920, 1083, 1083, 1244, 1244, 1399, 1399, 1549, 1549, 1693, 1693, 1830, 1830, 1960, 1960, 2082, 2082, 2197, 2197, 2301, 2301, 2398, 2398, 2485, 2485, 2562, 2562, 2629, 2629, 2686, 2686, 2732, 2732, 2767, 2767] },
            { "no_xing.mp3", 2, 44100, 384 * 1152, 0, [2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 0, 0, 0, 0] }
        };

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void Decode_Should_Match_Ffmpegs_Decode(string fixture, int channels, int sampleRate, long totalSamples, int ffmpegOffset, int[] ffmpegAt300)
        {
            var (info, samples) = DecodeFixture(fixture);

            info.Channels.Should().Be(channels);
            info.SampleRate.Should().Be(sampleRate);
            info.BitsPerSample.Should().Be(16);
            info.TotalSamples.Should().Be(totalSamples);
            samples.Should().HaveCount((int)(totalSamples * channels));
            var start = (ffmpegOffset + 300) * channels;
            for (var i = 0; i < ffmpegAt300.Length; i++)
            {
                samples[start + i].Should().BeInRange(ffmpegAt300[i] - 2, ffmpegAt300[i] + 2, $"sample {i} of the window at 300 in {fixture}");
            }
        }

        [Fact]
        public void Decode_Should_Match_Ffmpeg_On_Every_Sample()
        {
            if (Environment.GetEnvironmentVariable("EGGENCODER_FFMPEG_ORACLE") != "1")
            {
                return;
            }

            foreach (var fixture in new[] { "lsf_22050_stereo.mp3", "mpeg25_8000_mono.mp3", "mpeg25_11025_stereo.mp3", "mono_44100.mp3", "transients_44100.mp3", "cbr128.mp3", "vbr_q4.mp3", "tone.mp3" })
            {
                var (info, samples) = DecodeFixture(fixture);
                var reference = FfmpegDecode(Path.Combine(AppContext.BaseDirectory, "Codecs", "Mp3", fixture));
                var offset = FfmpegTrimmedStart * info.Channels;
                var worst = 0;
                for (var i = 0; i < reference.Length && offset + i < samples.Count; i++)
                {
                    worst = Math.Max(worst, Math.Abs(samples[offset + i] - reference[i]));
                }

                worst.Should().BeLessThanOrEqualTo(2, fixture);
            }
        }

        [Theory]
        [InlineData("cbr128.mp3")]
        [InlineData("vbr_q4.mp3")]
        [InlineData("tone.mp3")]
        public void Decode_Should_Give_The_Same_Samples_On_Any_Number_Of_Threads(string fixture)
        {
            var path = Fixture(fixture);
            var single = new List<int>();
            var info = Mp3Decoder.Decode(path, (block, _, _, _, _) => single.AddRange(block.ToArray()), 1);
            info.TotalSamples.Should().BeGreaterThan(Mp3StreamDecoder.SegmentFrames * 2 * 1152);

            foreach (var threads in new[] { 2, 3, 7 })
            {
                var parallel = new List<int>();
                Mp3Decoder.Decode(path, (block, _, _, _, _) => parallel.AddRange(block.ToArray()), threads);
                parallel.Should().Equal(single, $"{threads} threads");
            }
        }

        [Fact]
        public void WarmUpStart_Should_Cover_Two_Frames_And_A_Full_Bit_Reservoir()
        {
            var frames = Mp3StreamDecoder.FindAudioFrames(File.ReadAllBytes(Fixture("mpeg25_8000_mono.mp3")));

            Mp3StreamDecoder.WarmUpStart(frames, 0).Should().Be(0);
            Mp3StreamDecoder.WarmUpStart(frames, 1).Should().Be(0);
            var start = Mp3StreamDecoder.WarmUpStart(frames, 12);
            var bytes = 0;
            for (var i = start; i < 10; i++)
            {
                var header = frames[i].Header;
                bytes += header.FrameLength - 4 - header.SideInfoLength - (header.HasCrc ? 2 : 0);
            }

            bytes.Should().BeGreaterThanOrEqualTo(511);
        }

        [Fact]
        public void Decode_Layer_II_Should_Throw_NotSupportedException()
        {
            var act = () => Mp3Decoder.Decode(Fixture("layer2.mp3"), (_, _, _, _, _) => { });

            act.Should().Throw<NotSupportedException>().WithMessage("*Layer III*");
        }

        [Theory]
        [InlineData(0)]
        [InlineData(3)]
        [InlineData(4000)]
        public void Decode_Data_Without_Frames_Should_Throw_InvalidDataException(int length)
        {
            var noise = new byte[length];
            new Random(9).NextBytes(noise);
            for (var i = 0; i < noise.Length; i++)
            {
                noise[i] &= 0x7F;
            }

            var act = () => Mp3Decoder.Decode(new MemoryStream(noise), (_, _, _, _, _) => { });

            act.Should().Throw<InvalidDataException>();
        }

        [Fact]
        public void Decode_Stream_Cut_Mid_Frame_And_Started_Mid_Stream_Should_Still_Decode()
        {
            var bytes = File.ReadAllBytes(Fixture("cbr128.mp3"));
            var cut = bytes.AsSpan(20_000, 60_000).ToArray();
            var samples = new List<int>();

            var info = Mp3Decoder.Decode(new MemoryStream(cut), (block, _, _, _, _) => samples.AddRange(block.ToArray()), 1);

            info.Channels.Should().Be(2);
            samples.Should().HaveCount((int)(info.TotalSamples * 2));
            info.TotalSamples.Should().BeGreaterThan(40 * 1152);
            var tail = samples.Skip(5 * 1152 * 2).ToList();
            Math.Sqrt(tail.Average(s => (double)s * s)).Should().BeGreaterThan(500, "the bit reservoir refills after the first frames");
        }

        [Fact]
        public void Decode_Corrupted_Main_Data_Should_Conceal_And_Keep_The_Length()
        {
            var bytes = File.ReadAllBytes(Fixture("transients_44100.mp3"));
            var random = new Random(3);
            for (var i = 2000; i < bytes.Length; i += 37)
            {
                if (bytes[i] != 0xFF && bytes[i - 1] != 0xFF)
                {
                    bytes[i] = (byte)(random.Next(256) & 0xFE);
                }
            }

            var count = 0;
            var info = Mp3Decoder.Decode(new MemoryStream(bytes), (block, _, _, _, _) => count += block.Length);

            count.Should().Be((int)(info.TotalSamples * info.Channels));
        }

        [Fact]
        public void Decode_Should_Skip_An_Id3v2_Tag_And_Give_The_Same_Audio()
        {
            var bytes = File.ReadAllBytes(Fixture("mono_44100.mp3"));
            var tag = new byte[10 + 300];
            "ID3"u8.CopyTo(tag);
            tag[3] = 4;
            tag[8] = 2;
            tag[9] = 44;
            tag[200] = 0xFF;
            tag[201] = 0xFB;
            tag[202] = 0x90;
            tag[203] = 0x64;
            var tagged = tag.Concat(bytes).ToArray();

            var plain = Collect(new MemoryStream(bytes));
            var withTag = Collect(new MemoryStream(tagged));

            withTag.Should().Equal(plain);
        }

        internal static (Mp3StreamInfo Info, List<int> Samples) DecodeFixture(string fixture)
        {
            var samples = new List<int>();
            var info = Mp3Decoder.Decode(Fixture(fixture), (block, _, _, _, _) =>
            {
                foreach (var sample in block)
                {
                    samples.Add(sample);
                }
            });
            return (info, samples);
        }

        private static List<int> Collect(Stream stream)
        {
            var samples = new List<int>();
            Mp3Decoder.Decode(stream, (block, _, _, _, _) =>
            {
                foreach (var sample in block)
                {
                    samples.Add(sample);
                }
            });
            return samples;
        }

        private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Codecs", "Mp3", name);

        private static short[] FfmpegDecode(string path)
        {
            using var process = Process.Start(new ProcessStartInfo("ffmpeg", ["-v", "error", "-i", path, "-f", "s16le", "-"]) { RedirectStandardOutput = true })!;
            using var buffer = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(buffer);
            process.WaitForExit();
            var bytes = buffer.ToArray();
            var samples = new short[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2);
            return samples;
        }
    }
}
