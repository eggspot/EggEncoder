using System.Diagnostics;
using EggEncoder.Codecs.Flac;
using EggEncoder.Codecs.Wav;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Flac
{
    public class FlacFrameHeaderTest
    {
        private const int FullBlock = 4096;

        [Theory]
        [InlineData(8000, 4, -1)]
        [InlineData(16000, 5, -1)]
        [InlineData(22050, 6, -1)]
        [InlineData(24000, 7, -1)]
        [InlineData(32000, 8, -1)]
        [InlineData(44100, 9, -1)]
        [InlineData(48000, 10, -1)]
        [InlineData(88200, 1, -1)]
        [InlineData(96000, 11, -1)]
        [InlineData(176400, 2, -1)]
        [InlineData(192000, 3, -1)]
        [InlineData(12000, 12, 12)]
        [InlineData(11025, 13, 11025)]
        [InlineData(37800, 14, 3780)]
        public void Every_Frame_Header_Should_Carry_The_Sample_Rate_The_Way_libFLAC_Writes_It(int sampleRate, int expectedCode, int expectedTail)
        {
            var bytes = Encode(channels: 2, bitsPerSample: 16, sampleRate, frames: FullBlock * 2);

            var header = ReadFirstFrameHeader(bytes);

            header.SampleRateCode.Should().Be(expectedCode);
            header.SampleRateTail.Should().Be(expectedTail);
            header.SampleRateCode.Should().NotBe(0, "code 0 defers to STREAMINFO, which Apple's FLAC validator rejects");
        }

        [Theory]
        [InlineData(8, 1)]
        [InlineData(12, 2)]
        [InlineData(16, 4)]
        [InlineData(20, 5)]
        [InlineData(24, 6)]
        [InlineData(32, 7)]
        public void Every_Frame_Header_Should_Carry_The_Sample_Size(int bitsPerSample, int expectedCode)
        {
            var bytes = Encode(channels: 2, bitsPerSample, sampleRate: 48000, frames: FullBlock * 2);

            ReadFirstFrameHeader(bytes).SampleSizeCode.Should().Be(expectedCode);
        }

        [Fact]
        public void Block_Sizes_Should_Use_The_Standard_Codes_And_The_Last_Frame_Its_Own_Size()
        {
            var bytes = Encode(channels: 1, bitsPerSample: 16, sampleRate: 44100, frames: FullBlock + 1000);

            ReadFirstFrameHeader(bytes).BlockSizeCode.Should().Be(12);
            var frames = ReadFrameHeaders(bytes);
            frames.Should().HaveCount(2);
            frames[1].BlockSizeCode.Should().Be(7);
            frames[1].BlockSize.Should().Be(1000);
        }

        [Theory]
        [InlineData(16, 44100)]
        [InlineData(24, 48000)]
        public void Streaminfo_Should_Record_The_Real_Smallest_And_Largest_Frame(int bitsPerSample, int sampleRate)
        {
            var bytes = Encode(channels: 2, bitsPerSample, sampleRate, frames: (FullBlock * 3) + 500);

            var sizes = ReadFrameHeaders(bytes).Select(frame => frame.Length).ToArray();
            var minimum = (bytes[12] << 16) | (bytes[13] << 8) | bytes[14];
            var maximum = (bytes[15] << 16) | (bytes[16] << 8) | bytes[17];
            minimum.Should().Be(sizes.Min());
            maximum.Should().Be(sizes.Max());
        }

        [Theory]
        [InlineData(16, 44100)]
        [InlineData(24, 48000)]
        [InlineData(24, 44100)]
        public void Encoded_Files_Should_Decode_Back_Exactly(int bitsPerSample, int sampleRate)
        {
            var samples = Signal(channels: 2, bitsPerSample, frames: (FullBlock * 2) + 777);
            var bytes = Encode(samples, channels: 2, bitsPerSample, sampleRate);

            var decoded = new List<int>();
            using var stream = new MemoryStream(bytes);
            var info = FlacDecoder.Decode(stream, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

            info.SampleRate.Should().Be(sampleRate);
            decoded.Should().Equal(samples);
        }

        [Theory]
        [InlineData(16, 44100)]
        [InlineData(24, 48000)]
        [InlineData(24, 44100)]
        public void libFLAC_Should_Accept_The_Output_And_Write_The_Same_Frame_Header_Codes(int bitsPerSample, int sampleRate)
        {
            if (Environment.GetEnvironmentVariable("EGGENCODER_FLAC_PATH") is not { Length: > 0 } flac)
            {
                return;
            }

            var directory = Path.Combine(Path.GetTempPath(), $"flac_oracle_{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var samples = Signal(channels: 2, bitsPerSample, frames: (FullBlock * 4) + 123);
                var wav = Path.Combine(directory, "source.wav");
                using (var writer = WavWriter.Create(wav, 2, sampleRate, bitsPerSample, samples.Length / 2))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length / 2);
                    writer.Finish();
                }

                var ours = Path.Combine(directory, "ours.flac");
                File.WriteAllBytes(ours, Encode(samples, channels: 2, bitsPerSample, sampleRate));
                var reference = Path.Combine(directory, "reference.flac");

                Run(flac, "-t", ours).Should().Be(0, "libFLAC must decode the file and verify its MD5");
                Run(flac, "-8", "--no-padding", "--no-seektable", "-f", "-o", reference, wav).Should().Be(0);

                var oursHeaders = ReadFrameHeaders(File.ReadAllBytes(ours));
                var referenceHeaders = ReadFrameHeaders(File.ReadAllBytes(reference));
                oursHeaders.Should().HaveSameCount(referenceHeaders);
                for (var i = 0; i < oursHeaders.Count; i++)
                {
                    oursHeaders[i].BlockSizeCode.Should().Be(referenceHeaders[i].BlockSizeCode, $"frame {i}");
                    oursHeaders[i].SampleRateCode.Should().Be(referenceHeaders[i].SampleRateCode, $"frame {i}");
                    oursHeaders[i].SampleSizeCode.Should().Be(referenceHeaders[i].SampleSizeCode, $"frame {i}");
                    oursHeaders[i].BlockSize.Should().Be(referenceHeaders[i].BlockSize, $"frame {i}");
                }
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private static int Run(string executable, params string[] arguments)
        {
            using var process = Process.Start(new ProcessStartInfo(executable, arguments) { RedirectStandardError = true, RedirectStandardOutput = true })!;
            process.StandardError.ReadToEnd();
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode;
        }

        private static byte[] Encode(int channels, int bitsPerSample, int sampleRate, int frames) =>
            Encode(Signal(channels, bitsPerSample, frames), channels, bitsPerSample, sampleRate);

        private static byte[] Encode(int[] samples, int channels, int bitsPerSample, int sampleRate)
        {
            using var stream = new MemoryStream();
            using (var session = FlacEncoder.OpenSession(stream, channels, bitsPerSample, sampleRate))
            {
                session.WriteInterleavedSamples(samples, samples.Length / channels);
                session.Finish();
            }

            return stream.ToArray();
        }

        private static int[] Signal(int channels, int bitsPerSample, int frames)
        {
            var random = new Random(bitsPerSample);
            var amplitude = (1 << (bitsPerSample - 2)) - 1;
            var samples = new int[frames * channels];
            for (var i = 0; i < frames; i++)
            {
                for (var c = 0; c < channels; c++)
                {
                    var tone = Math.Sin(2 * Math.PI * (440 + (110 * c)) * i / 44100.0);
                    samples[(i * channels) + c] = (int)((tone * 0.6 * amplitude) + ((random.NextDouble() - 0.5) * 0.2 * amplitude));
                }
            }

            return samples;
        }

        private static FrameHeader ReadFirstFrameHeader(byte[] bytes) => ReadFrameHeaders(bytes)[0];

        private static int FirstFrameOffset(byte[] bytes)
        {
            var offset = 4;
            while (true)
            {
                var last = (bytes[offset] & 0x80) != 0;
                var length = (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
                offset += 4 + length;
                if (last)
                {
                    return offset;
                }
            }
        }

        private static List<FrameHeader> ReadFrameHeaders(byte[] bytes)
        {
            var headers = new List<FrameHeader>();
            var offset = FirstFrameOffset(bytes);
            while (offset < bytes.Length)
            {
                var header = ParseHeader(bytes, offset);
                var next = FindNextSync(bytes, offset + header.HeaderLength);
                headers.Add(header with { Length = next - offset });
                offset = next;
            }

            return headers;
        }

        private static int FindNextSync(byte[] bytes, int from)
        {
            for (var i = from; i + 1 < bytes.Length; i++)
            {
                if (bytes[i] == 0xFF && (bytes[i + 1] & 0xFE) == 0xF8 && LooksLikeHeader(bytes, i))
                {
                    return i;
                }
            }

            return bytes.Length;
        }

        private static bool LooksLikeHeader(byte[] bytes, int offset)
        {
            try
            {
                var header = ParseHeader(bytes, offset);
                return FlacCrc.Crc8(bytes, offset, offset + header.HeaderLength - 1) == bytes[offset + header.HeaderLength - 1];
            }
            catch (IndexOutOfRangeException)
            {
                return false;
            }
        }

        private static FrameHeader ParseHeader(byte[] bytes, int offset)
        {
            ((bytes[offset] << 8) | (bytes[offset + 1] & 0xFE)).Should().Be(0xFFF8);
            var blockSizeCode = bytes[offset + 2] >> 4;
            var sampleRateCode = bytes[offset + 2] & 0x0F;
            var sampleSizeCode = (bytes[offset + 3] >> 1) & 0x07;
            var position = offset + 4;
            var lead = bytes[position];
            var extra = lead < 0x80 ? 0 : lead < 0xE0 ? 1 : lead < 0xF0 ? 2 : lead < 0xF8 ? 3 : lead < 0xFC ? 4 : 5;
            position += 1 + extra;
            var blockSize = blockSizeCode switch
            {
                1 => 192,
                >= 2 and <= 5 => 576 << (blockSizeCode - 2),
                6 => bytes[position++] + 1,
                7 => ((bytes[position++] << 8) | bytes[position++]) + 1,
                >= 8 => 256 << (blockSizeCode - 8),
                _ => 0,
            };
            var sampleRateTail = sampleRateCode switch
            {
                12 => bytes[position++],
                13 or 14 => (bytes[position++] << 8) | bytes[position++],
                _ => -1,
            };

            return new FrameHeader(blockSizeCode, sampleRateCode, sampleSizeCode, blockSize, sampleRateTail, position + 1 - offset, 0);
        }

        private readonly record struct FrameHeader(int BlockSizeCode, int SampleRateCode, int SampleSizeCode, int BlockSize, int SampleRateTail, int HeaderLength, int Length);
    }
}
