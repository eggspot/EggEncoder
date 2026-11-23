using System.Buffers.Binary;
using System.Text;
using EggEncoder.Codecs.Mp3;
using EggEncoder.Codecs.Wav;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EggEncoder.UnitTests.Codecs.Wav
{
    public class WavReaderPartialFrameTest
    {
        private const int Channels = 2;
        private const int SampleRate = 44100;
        private const int Frames = 1000;
        private const ushort PcmFormat = 1;
        private const ushort FloatFormat = 3;

        [Theory]
        [InlineData(16, PcmFormat, 2)]
        [InlineData(16, PcmFormat, 1)]
        [InlineData(16, PcmFormat, 3)]
        [InlineData(24, PcmFormat, 3)]
        [InlineData(24, PcmFormat, 5)]
        [InlineData(8, PcmFormat, 1)]
        [InlineData(32, PcmFormat, 6)]
        [InlineData(32, FloatFormat, 4)]
        public void Data_Chunk_Ending_In_Part_Of_A_Frame_Should_Drop_That_Part(int bitsPerSample, ushort formatTag, int leftoverBytes)
        {
            var samples = Samples(bitsPerSample, formatTag);
            using var stream = new MemoryStream(BuildWav(samples, bitsPerSample, formatTag, leftoverBytes, trailingZeroBytes: 0));

            using var reader = WavReader.Open(stream);

            reader.TotalSamples.Should().Be(Frames);
            ReadAll(reader).Should().Equal(Expected(samples, formatTag));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(38)]
        [InlineData(4096)]
        public void Zero_Padding_After_The_Data_Chunk_Should_End_The_Chunk_Walk(int trailingZeroBytes)
        {
            var samples = Samples(16, PcmFormat);
            using var stream = new MemoryStream(BuildWav(samples, 16, PcmFormat, leftoverBytes: 0, trailingZeroBytes));

            using var reader = WavReader.Open(stream);

            reader.TotalSamples.Should().Be(Frames);
            ReadAll(reader).Should().Equal(samples);
        }

        [Fact]
        public void Tags_Before_Zero_Padding_After_The_Data_Should_Still_Be_Read()
        {
            var samples = Samples(16, PcmFormat);
            using var stream = new MemoryStream(BuildWav(samples, 16, PcmFormat, leftoverBytes: 2, trailingZeroBytes: 38, title: "Title"));

            using var reader = WavReader.Open(stream);

            reader.Tags.Should().ContainKey("INAM").WhoseValue.Should().Be("Title");
            ReadAll(reader).Should().Equal(samples);
        }

        [Fact]
        public void Converting_A_Wav_With_Half_A_Frame_And_Zero_Padding_After_Its_Data_Should_Keep_All_Whole_Frames()
        {
            var samples = Samples(16, PcmFormat);
            var source = Path.Combine(Path.GetTempPath(), $"partial_frame_{Guid.NewGuid():N}.wav");
            var destination = Path.ChangeExtension(source, ".mp3");
            File.WriteAllBytes(source, BuildWav(samples, 16, PcmFormat, leftoverBytes: 2, trailingZeroBytes: 38));
            try
            {
                var encoder = new NativeEncoder(NullLogger<NativeEncoder>.Instance);

                encoder.ConvertFile(source, destination);

                var decodedFrames = 0L;
                Mp3Decoder.Decode(destination, (block, channels, _, _, _) => decodedFrames += block.Length / channels);
                decodedFrames.Should().BeGreaterThanOrEqualTo(Frames);
            }
            finally
            {
                File.Delete(source);
                File.Delete(destination);
            }
        }

        [Fact]
        public void A_File_Cut_Inside_Its_Format_Chunk_Should_Still_Be_Reported_As_Truncated()
        {
            var whole = BuildWav(Samples(16, PcmFormat), 16, PcmFormat, leftoverBytes: 0, trailingZeroBytes: 0);
            using var stream = new MemoryStream(whole[..30]);

            var open = () => WavReader.Open(stream);

            open.Should().Throw<InvalidDataException>().WithMessage("*truncated*");
        }

        private static int[] Samples(int bitsPerSample, ushort formatTag)
        {
            var samples = new int[Frames * Channels];
            var amplitude = formatTag == FloatFormat ? 0.5 * int.MaxValue : 0.4 * (1L << (bitsPerSample - 1));
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (int)(amplitude * Math.Sin(i * 0.05));
            }

            return samples;
        }

        private static float ToFloatSample(int sample) => (float)(sample / (double)int.MaxValue);

        private static int[] Expected(int[] samples, ushort formatTag) => formatTag == FloatFormat
            ? [.. samples.Select(sample => EggEncoder.Pcm.FloatSampleConverter.ClampToNativeInt32(ToFloatSample(sample)))]
            : samples;

        private static int[] ReadAll(WavReader reader)
        {
            var all = new List<int>();
            var buffer = new int[256 * Channels];
            int frames;
            while ((frames = reader.ReadInterleavedSamples(buffer, 256)) > 0)
            {
                all.AddRange(buffer.Take(frames * Channels));
            }

            return [.. all];
        }

        private static byte[] BuildWav(int[] samples, int bitsPerSample, ushort formatTag, int leftoverBytes, int trailingZeroBytes, string? title = null)
        {
            var bytesPerSample = bitsPerSample / 8;
            var data = new byte[(samples.Length * bytesPerSample) + leftoverBytes];
            for (var i = 0; i < samples.Length; i++)
            {
                var slot = data.AsSpan(i * bytesPerSample, bytesPerSample);
                switch (bitsPerSample)
                {
                    case 8:
                        slot[0] = (byte)(samples[i] + 128);
                        break;
                    case 16:
                        BinaryPrimitives.WriteInt16LittleEndian(slot, (short)samples[i]);
                        break;
                    case 24:
                        slot[0] = (byte)samples[i];
                        slot[1] = (byte)(samples[i] >> 8);
                        slot[2] = (byte)(samples[i] >> 16);
                        break;
                    default:
                        if (formatTag == FloatFormat)
                        {
                            BinaryPrimitives.WriteSingleLittleEndian(slot, ToFloatSample(samples[i]));
                        }
                        else
                        {
                            BinaryPrimitives.WriteInt32LittleEndian(slot, samples[i]);
                        }

                        break;
                }
            }

            for (var i = samples.Length * bytesPerSample; i < data.Length; i++)
            {
                data[i] = 0x5A;
            }

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(0u);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(18u);
            writer.Write(formatTag);
            writer.Write((ushort)Channels);
            writer.Write((uint)SampleRate);
            writer.Write((uint)(SampleRate * Channels * bytesPerSample));
            writer.Write((ushort)(Channels * bytesPerSample));
            writer.Write((ushort)bitsPerSample);
            writer.Write((ushort)0);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write((uint)data.Length);
            writer.Write(data);
            if (data.Length % 2 == 1)
            {
                writer.Write((byte)0);
            }

            if (title is not null)
            {
                var text = Encoding.ASCII.GetBytes(title + "\0");
                writer.Write(Encoding.ASCII.GetBytes("LIST"));
                writer.Write((uint)(4 + 8 + text.Length + (text.Length % 2)));
                writer.Write(Encoding.ASCII.GetBytes("INFO"));
                writer.Write(Encoding.ASCII.GetBytes("INAM"));
                writer.Write((uint)text.Length);
                writer.Write(text);
                if (text.Length % 2 == 1)
                {
                    writer.Write((byte)0);
                }
            }

            writer.Write(new byte[trailingZeroBytes]);
            writer.Flush();
            var bytes = stream.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
            return bytes;
        }
    }
}
