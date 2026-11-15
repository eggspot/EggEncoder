using System.Diagnostics;
using EggEncoder.Codecs.Jpeg;
using EggEncoder.UnitTests.TestUtilities;
using EggEncoder.Video;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Jpeg
{
    public class JpegEncoderTest
    {
        public static readonly int[] FfmpegScale3Table =
        [
            8, 6, 6, 7, 6, 7, 8, 8, 8, 8, 8, 8, 9, 9, 9, 10, 10, 10, 9, 9, 9, 9, 10, 10, 10, 10, 10, 10, 12, 12, 12, 10,
            10, 10, 10, 10, 10, 10, 12, 12, 12, 12, 13, 14, 13, 13, 13, 12, 13, 14, 14, 15, 15, 15, 18, 18, 17, 17, 21,
            21, 21, 25, 25, 31
        ];

        [Fact]
        public void Encode_Smooth_Frame_Should_Write_A_Baseline_Jfif_File_That_Decodes_Close_To_The_Source()
        {
            using var frame = Gradient(64, 48, VideoColorRange.Full, VideoColorMatrix.Bt601);

            var file = JpegEncoder.Encode(frame, 2);

            file[..2].Should().Equal(0xFF, 0xD8);
            file[^2..].Should().Equal(0xFF, 0xD9);
            var decoded = BaselineJpegDecoder.Decode(file);
            decoded.SawJfif.Should().BeTrue();
            decoded.Width.Should().Be(64);
            decoded.Height.Should().Be(48);
            decoded.PlaneSizes.Should().Equal((64, 48), (32, 24), (32, 24));
            Psnr(decoded.Planes[0], Plane(frame, VideoPlane.Y)).Should().BeGreaterThan(40);
            Psnr(decoded.Planes[1], Plane(frame, VideoPlane.U)).Should().BeGreaterThan(40);
            Psnr(decoded.Planes[2], Plane(frame, VideoPlane.V)).Should().BeGreaterThan(40);
        }

        [Fact]
        public void Encode_Scale_3_Should_Write_The_Quantisation_Table_Ffmpeg_Writes_For_Q_3()
        {
            using var frame = Gradient(16, 16, VideoColorRange.Full, VideoColorMatrix.Bt601);

            var decoded = BaselineJpegDecoder.Decode(JpegEncoder.Encode(frame));

            decoded.QuantizationTablesInZigZagOrder.Should().ContainSingle();
            decoded.QuantizationTablesInZigZagOrder[0].Should().Equal(FfmpegScale3Table);
        }

        [Fact]
        public void Encode_Higher_Scale_Should_Give_A_Smaller_File_And_Lower_Psnr()
        {
            using var frame = Noise(96, 64, 3);

            var fine = JpegEncoder.Encode(frame, 2);
            var coarse = JpegEncoder.Encode(frame, 20);

            coarse.Length.Should().BeLessThan(fine.Length);
            var y = Plane(frame, VideoPlane.Y);
            Psnr(BaselineJpegDecoder.Decode(coarse).Planes[0], y).Should().BeLessThan(Psnr(BaselineJpegDecoder.Decode(fine).Planes[0], y));
        }

        [Fact]
        public void Encode_Noise_Should_Stuff_Ff_Bytes_And_Still_Decode()
        {
            using var frame = Noise(64, 64, 7);

            var file = JpegEncoder.Encode(frame, 1);

            var decoded = BaselineJpegDecoder.Decode(file);
            Psnr(decoded.Planes[0], Plane(frame, VideoPlane.Y)).Should().BeGreaterThan(30);
        }

        [Theory]
        [InlineData(17, 9)]
        [InlineData(1, 1)]
        [InlineData(33, 70)]
        public void Encode_Odd_Sizes_Should_Keep_The_Size_And_Round_Chroma_Up(int width, int height)
        {
            using var frame = Gradient(width, height, VideoColorRange.Full, VideoColorMatrix.Bt601);

            var decoded = BaselineJpegDecoder.Decode(JpegEncoder.Encode(frame, 2));

            decoded.Width.Should().Be(width);
            decoded.Height.Should().Be(height);
            decoded.PlaneSizes[1].Should().Be(((width + 1) / 2, (height + 1) / 2));
            Psnr(decoded.Planes[0], Plane(frame, VideoPlane.Y)).Should().BeGreaterThan(35);
        }

        [Theory]
        [InlineData(VideoColorRange.Limited, VideoColorMatrix.Bt709)]
        [InlineData(VideoColorRange.Limited, VideoColorMatrix.Bt601)]
        [InlineData(VideoColorRange.Full, VideoColorMatrix.Bt709)]
        public void Encode_Other_Range_Or_Matrix_Should_Convert_To_Full_Range_Bt601(VideoColorRange range, VideoColorMatrix matrix)
        {
            using var frame = new VideoFramePool().Rent(32, 32, range, matrix);
            frame.Fill(YuvColor.FromRgb(200, 100, 50, matrix, range));
            var expected = YuvColor.FromRgb(200, 100, 50, VideoColorMatrix.Bt601, VideoColorRange.Full);

            var decoded = BaselineJpegDecoder.Decode(JpegEncoder.Encode(frame, 1));

            decoded.Planes[0].Should().OnlyContain(v => Math.Abs(v - expected.Y) <= 2);
            decoded.Planes[1].Should().OnlyContain(v => Math.Abs(v - expected.U) <= 2);
            decoded.Planes[2].Should().OnlyContain(v => Math.Abs(v - expected.V) <= 2);
        }

        [Fact]
        public void Encode_Into_Stream_Should_Leave_It_Open_And_Write_From_Its_Position()
        {
            using var frame = Gradient(16, 16, VideoColorRange.Full, VideoColorMatrix.Bt601);
            using var stream = new MemoryStream();
            stream.WriteByte(42);

            JpegEncoder.Encode(frame, stream);

            stream.CanWrite.Should().BeTrue();
            stream.ToArray()[1..].Should().Equal(JpegEncoder.Encode(frame));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(32)]
        public void Encode_Scale_Out_Of_Range_Should_Throw(int scale)
        {
            using var frame = Gradient(16, 16, VideoColorRange.Full, VideoColorMatrix.Bt601);

            var act = () => JpegEncoder.Encode(frame, scale);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Encode_Null_Arguments_Should_Throw()
        {
            using var frame = Gradient(16, 16, VideoColorRange.Full, VideoColorMatrix.Bt601);

            ((Action)(() => JpegEncoder.Encode(null!, new MemoryStream()))).Should().Throw<ArgumentNullException>();
            ((Action)(() => JpegEncoder.Encode(frame, null!))).Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Encode_Should_Decode_In_Ffmpeg_To_What_The_Reference_Decoder_Gives()
        {
            if (Environment.GetEnvironmentVariable("EGGENCODER_FFMPEG_ORACLE") != "1")
            {
                return;
            }

            using var frame = Noise(48, 32, 11);
            var directory = Directory.CreateTempSubdirectory("eggencoder-jpeg-").FullName;
            var jpeg = Path.Combine(directory, "frame.jpg");
            var raw = Path.Combine(directory, "frame.yuv");
            File.WriteAllBytes(jpeg, JpegEncoder.Encode(frame, 3));

            using (var process = Process.Start(new ProcessStartInfo("ffmpeg", ["-v", "error", "-y", "-i", jpeg, "-f", "rawvideo", "-pix_fmt", "yuvj420p", raw]) { RedirectStandardError = true })!)
            {
                process.WaitForExit();
                process.ExitCode.Should().Be(0, process.StandardError.ReadToEnd());
            }

            var ffmpeg = File.ReadAllBytes(raw);
            var reference = BaselineJpegDecoder.Decode(File.ReadAllBytes(jpeg));
            var expected = reference.Planes[0].Concat(reference.Planes[1]).Concat(reference.Planes[2]).ToArray();
            ffmpeg.Should().HaveCount(expected.Length);
            ffmpeg.Zip(expected).Should().OnlyContain(pair => Math.Abs(pair.First - pair.Second) <= 1);
        }

        internal static VideoFrame Gradient(int width, int height, VideoColorRange range, VideoColorMatrix matrix)
        {
            var frame = new VideoFramePool().Rent(width, height, range, matrix);
            for (var y = 0; y < height; y++)
            {
                var row = frame.GetRow(VideoPlane.Y, y);
                for (var x = 0; x < width; x++)
                {
                    row[x] = (byte)(40 + (x * 150 / Math.Max(1, width)) + (y * 50 / Math.Max(1, height)));
                }
            }

            for (var y = 0; y < frame.ChromaHeight; y++)
            {
                var u = frame.GetRow(VideoPlane.U, y);
                var v = frame.GetRow(VideoPlane.V, y);
                for (var x = 0; x < frame.ChromaWidth; x++)
                {
                    u[x] = (byte)(90 + (x * 60 / Math.Max(1, frame.ChromaWidth)));
                    v[x] = (byte)(170 - (y * 60 / Math.Max(1, frame.ChromaHeight)));
                }
            }

            return frame;
        }

        private static VideoFrame Noise(int width, int height, int seed)
        {
            var random = new Random(seed);
            var frame = new VideoFramePool().Rent(width, height, VideoColorRange.Full, VideoColorMatrix.Bt601);
            foreach (var plane in new[] { VideoPlane.Y, VideoPlane.U, VideoPlane.V })
            {
                var rows = plane == VideoPlane.Y ? height : frame.ChromaHeight;
                for (var y = 0; y < rows; y++)
                {
                    random.NextBytes(frame.GetRow(plane, y));
                }
            }

            return frame;
        }

        internal static byte[] Plane(VideoFrame frame, VideoPlane plane)
        {
            var width = plane == VideoPlane.Y ? frame.Width : frame.ChromaWidth;
            var height = plane == VideoPlane.Y ? frame.Height : frame.ChromaHeight;
            var result = new byte[width * height];
            for (var y = 0; y < height; y++)
            {
                frame.GetRow(plane, y)[..width].CopyTo(result.AsSpan(y * width));
            }

            return result;
        }

        internal static double Psnr(byte[] actual, byte[] expected)
        {
            actual.Should().HaveCount(expected.Length);
            var error = 0.0;
            for (var i = 0; i < actual.Length; i++)
            {
                var d = actual[i] - expected[i];
                error += d * d;
            }

            return error == 0 ? 100 : 10 * Math.Log10(255.0 * 255.0 / (error / actual.Length));
        }
    }
}
