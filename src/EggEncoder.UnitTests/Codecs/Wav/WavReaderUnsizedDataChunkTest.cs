using System.Buffers.Binary;
using System.Text;
using EggEncoder.Codecs;
using EggEncoder.Codecs.Mp3;
using EggEncoder.Codecs.Wav;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Wav
{
    public class WavReaderUnsizedDataChunkTest
    {
        private const int Channels = 2;
        private const int SampleRate = 44100;
        private const int Frames = 1000;

        [Theory]
        [InlineData(0u)]
        [InlineData(0xFFFFFFFFu)]
        [InlineData(1_000_000u)]
        public void Pcm_Data_Chunk_Without_A_Usable_Size_Should_Run_To_The_End_Of_The_Stream(uint declaredDataSize)
        {
            var samples = Samples();
            using var stream = new MemoryStream(StreamingToolWav(samples, declaredDataSize, trailingBytes: 3));

            using var reader = WavReader.Open(stream);

            reader.TotalSamples.Should().Be(Frames);
            ReadAll(reader).Should().Equal(samples);
        }

        [Fact]
        public void Empty_Data_Chunk_Followed_By_A_Tag_Chunk_Should_Stay_Empty()
        {
            using var stream = new MemoryStream();
            using (var writer = WavWriter.Create(stream, Channels, SampleRate, 16, 0, tags: new Dictionary<string, string> { ["INAM"] = "Title" }))
            {
                writer.Finish();
            }

            stream.Position = 0;
            using var reader = WavReader.Open(stream);

            reader.TotalSamples.Should().Be(0);
            reader.Tags.Should().ContainKey("INAM");
        }

        [Fact]
        public void Converting_A_Wav_With_An_Unsized_Data_Chunk_Should_Write_All_Of_Its_Audio()
        {
            var samples = Samples();
            var source = Path.Combine(Path.GetTempPath(), $"unsized_{Guid.NewGuid():N}.wav");
            var destination = Path.ChangeExtension(source, ".mp3");
            File.WriteAllBytes(source, StreamingToolWav(samples, declaredDataSize: 0, trailingBytes: 0));
            try
            {
                AudioCutter.Convert(source, destination);

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
        public void Converting_A_Wav_With_No_Samples_Should_Throw_And_Write_Nothing()
        {
            var source = Path.Combine(Path.GetTempPath(), $"empty_{Guid.NewGuid():N}.wav");
            var destination = Path.ChangeExtension(source, ".mp3");
            File.WriteAllBytes(source, StreamingToolWav([], declaredDataSize: 0, trailingBytes: 0));
            try
            {
                var convert = () => AudioCutter.Convert(source, destination);

                convert.Should().Throw<InvalidDataException>().WithMessage($"*{source}*no audio samples*");
                File.Exists(destination).Should().BeFalse();
            }
            finally
            {
                File.Delete(source);
                File.Delete(destination);
            }
        }

        [Fact]
        public void Cutting_A_Wav_With_No_Samples_Should_Throw()
        {
            var source = Path.Combine(Path.GetTempPath(), $"empty_{Guid.NewGuid():N}.wav");
            var destination = Path.ChangeExtension(source, ".mp3");
            File.WriteAllBytes(source, StreamingToolWav([], declaredDataSize: 0, trailingBytes: 0));
            try
            {
                var cut = () => AudioCutter.Cut(source, destination, 0, 10);

                cut.Should().Throw<InvalidDataException>().WithMessage("*no audio samples*");
                File.Exists(destination).Should().BeFalse();
            }
            finally
            {
                File.Delete(source);
                File.Delete(destination);
            }
        }

        [Fact]
        public void Converting_A_Stream_With_No_Samples_Should_Throw()
        {
            using var source = new MemoryStream(StreamingToolWav([], declaredDataSize: 0, trailingBytes: 0));
            using var destination = new MemoryStream();

            var convert = () => AudioCutter.Convert(source, AudioFormat.Wav, destination, AudioFormat.Wav);

            convert.Should().Throw<InvalidDataException>().WithMessage("*no audio samples*");
            destination.Length.Should().Be(0);
        }

        private static int[] Samples()
        {
            var samples = new int[Frames * Channels];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (int)(12000 * Math.Sin(i * 0.05)) - (i % 7);
            }

            return samples;
        }

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

        private static byte[] StreamingToolWav(int[] samples, uint declaredDataSize, int trailingBytes)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(0u);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(18u);
            writer.Write((ushort)1);
            writer.Write((ushort)Channels);
            writer.Write((uint)SampleRate);
            writer.Write((uint)(SampleRate * Channels * 2));
            writer.Write((ushort)(Channels * 2));
            writer.Write((ushort)16);
            writer.Write((ushort)0);
            writer.Write(Encoding.ASCII.GetBytes("fact"));
            writer.Write(4u);
            writer.Write(0u);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(declaredDataSize);
            var pcm = new byte[2];
            foreach (var sample in samples)
            {
                BinaryPrimitives.WriteInt16LittleEndian(pcm, (short)sample);
                writer.Write(pcm);
            }

            writer.Write(new byte[trailingBytes]);
            writer.Flush();
            return stream.ToArray();
        }
    }
}
