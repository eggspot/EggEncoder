using EggEncoder.Video;
using EggEncoder.Video.Filters;
using FluentAssertions;
using static EggEncoder.UnitTests.Video.Filters.FrameTestSupport;

namespace EggEncoder.UnitTests.Video.Filters
{
    public class IdentityScaleTest
    {
        public static TheoryData<ScaleKernel, int, int> Sizes => new()
        {
            { ScaleKernel.Bilinear, 64, 48 },
            { ScaleKernel.Bicubic, 64, 48 },
            { ScaleKernel.Lanczos, 64, 48 },
            { ScaleKernel.Bilinear, 37, 23 },
            { ScaleKernel.Bicubic, 37, 23 },
            { ScaleKernel.Lanczos, 37, 23 }
        };

        [Theory]
        [MemberData(nameof(Sizes))]
        public void The_Scaler_At_The_Same_Size_Should_Copy_Every_Sample(ScaleKernel kernel, int width, int height)
        {
            using var frame = Noise(width, height);
            using var output = VideoFrame.Create(width, height, frame.Range, frame.Matrix);
            var (luma, chroma) = PlaneScaler.Create(kernel, width, height, new VideoRectangle(0, 0, width, height), width, height);

            luma.Scale(frame.Buffer, frame.OffsetY, frame.StrideY, output.Buffer, output.OffsetY, output.StrideY, 1);
            chroma.Scale(frame.Buffer, frame.OffsetU, frame.StrideUV, output.Buffer, output.OffsetU, output.StrideUV, 1);
            chroma.Scale(frame.Buffer, frame.OffsetV, frame.StrideUV, output.Buffer, output.OffsetV, output.StrideUV, 1);

            Planes(output).Should().Equal(Planes(frame));
        }

        [Theory]
        [MemberData(nameof(Sizes))]
        public void ScaleTransform_At_The_Same_Size_Should_Pass_The_Frame_Through(ScaleKernel kernel, int width, int height)
        {
            var frame = Noise(width, height);
            frame.Timestamp = TimeSpan.FromSeconds(1.5);
            var expected = Planes(frame);
            VideoFrame? emitted = null;

            new ScaleTransform(width, height, kernel).Apply(frame, f => emitted = f);
            new ScaleTransform(width, height, kernel, new VideoRectangle(0, 0, width, height)).Apply(emitted!, f => emitted = f);

            using (emitted)
            {
                emitted.Should().BeSameAs(frame);
                emitted!.Timestamp.Should().Be(TimeSpan.FromSeconds(1.5));
                Planes(emitted).Should().Equal(expected);
            }
        }

        [Fact]
        public void A_Cropped_Source_Of_The_Output_Size_Should_Still_Be_Scaled()
        {
            using var frame = Noise(64, 48);
            VideoFrame? emitted = null;

            new ScaleTransform(32, 24, ScaleKernel.Bicubic, new VideoRectangle(16, 12, 32, 24)).Apply(frame.Clone(), f => emitted = f);

            using (emitted)
            {
                emitted.Should().NotBeSameAs(frame);
                emitted!.Width.Should().Be(32);
                emitted.GetRow(VideoPlane.Y, 0).ToArray().Should().Equal(frame.GetRow(VideoPlane.Y, 12).Slice(16, 32).ToArray());
            }
        }

        private static VideoFrame Noise(int width, int height)
        {
            var random = new Random(width * 1000 + height);
            return Pattern(width, height, (_, _, _) => (byte)random.Next(256));
        }

        private static List<byte> Planes(VideoFrame frame)
        {
            var bytes = new List<byte>();
            for (var y = 0; y < frame.Height; y++)
            {
                bytes.AddRange(frame.GetRow(VideoPlane.Y, y).ToArray());
            }

            for (var y = 0; y < frame.ChromaHeight; y++)
            {
                bytes.AddRange(frame.GetRow(VideoPlane.U, y).ToArray());
                bytes.AddRange(frame.GetRow(VideoPlane.V, y).ToArray());
            }

            return bytes;
        }
    }
}
