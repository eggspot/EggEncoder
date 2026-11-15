using EggEncoder.Codecs.Mpeg2;
using EggEncoder.Video;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Mpeg2
{
    public class Mpeg2VideoDecoderTest
    {
        private const int Width = 64;
        private const int Height = 48;
        private const int FrameBytes = Width * Height * 3 / 2;

        private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Codecs", "Mpeg2", name));

        [Theory]
        [InlineData("progressive")]
        [InlineData("interlaced")]
        public void Decode_Should_Match_Ffmpeg_Within_Idct_Accuracy(string name)
        {
            var expected = Fixture($"{name}.yuv");

            var frames = DecodeAll(Fixture($"{name}.m2v"), 777);

            frames.Should().HaveCount(expected.Length / FrameBytes);
            for (var i = 0; i < frames.Count; i++)
            {
                var reference = expected.AsSpan(i * FrameBytes, FrameBytes);
                Psnr(frames[i], reference).Should().BeGreaterThan(40, $"frame {i} of {name}");
            }
        }

        [Fact]
        public void Chunk_Size_Should_Not_Change_The_Output()
        {
            var stream = Fixture("interlaced.m2v");

            var whole = DecodeAll(stream, stream.Length);
            var small = DecodeAll(stream, 7);

            small.Select(Convert.ToHexString).Should().Equal(whole.Select(Convert.ToHexString));
        }

        [Fact]
        public void Decoder_Should_Report_Size_And_Frame_Rate()
        {
            using var decoder = new Mpeg2VideoDecoder();
            decoder.Decode(Fixture("progressive.m2v"), TimeSpan.Zero, f => f.Dispose());

            (decoder.Width, decoder.Height).Should().Be((Width, Height));
            decoder.FrameRate.Should().Be(new VideoFrameRate(10));
        }

        [Fact]
        public void Mpeg1_Streams_Should_Be_Refused()
        {
            using var decoder = new Mpeg2VideoDecoder();

            decoder.Invoking(d =>
            {
                d.Decode(Fixture("mpeg1.m1v"), TimeSpan.Zero, f => f.Dispose());
                d.Flush(f => f.Dispose());
            }).Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void Damaged_Streams_Should_Only_Throw_InvalidData_Or_NotSupported()
        {
            var original = Fixture("interlaced.m2v");
            var random = new Random(3);
            for (var round = 0; round < 300; round++)
            {
                var bytes = (byte[])original.Clone();
                for (var flips = 0; flips < 4; flips++)
                {
                    bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));
                }

                using var decoder = new Mpeg2VideoDecoder();
                try
                {
                    decoder.Decode(bytes, TimeSpan.Zero, f => f.Dispose());
                    decoder.Flush(f => f.Dispose());
                }
                catch (InvalidDataException)
                {
                }
                catch (NotSupportedException)
                {
                }
            }
        }

        private static List<byte[]> DecodeAll(byte[] stream, int chunkSize)
        {
            var frames = new List<byte[]>();
            using var decoder = new Mpeg2VideoDecoder();
            void Collect(VideoFrame frame)
            {
                var bytes = new List<byte>();
                for (var y = 0; y < frame.Height; y++)
                {
                    bytes.AddRange(frame.GetRow(VideoPlane.Y, y).ToArray());
                }

                foreach (var plane in new[] { VideoPlane.U, VideoPlane.V })
                {
                    for (var y = 0; y < frame.ChromaHeight; y++)
                    {
                        bytes.AddRange(frame.GetRow(plane, y).ToArray());
                    }
                }

                frames.Add([.. bytes]);
                frame.Dispose();
            }

            var index = 0;
            for (var offset = 0; offset < stream.Length; offset += chunkSize)
            {
                decoder.Decode(stream.AsSpan(offset, Math.Min(chunkSize, stream.Length - offset)), TimeSpan.FromMilliseconds(100 * index++), Collect);
            }

            decoder.Flush(Collect);
            return frames;
        }

        private static double Psnr(byte[] actual, ReadOnlySpan<byte> expected)
        {
            double squared = 0;
            for (var i = 0; i < actual.Length; i++)
            {
                var difference = actual[i] - expected[i];
                squared += difference * difference;
            }

            return squared == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 * actual.Length / squared);
        }
    }
}
