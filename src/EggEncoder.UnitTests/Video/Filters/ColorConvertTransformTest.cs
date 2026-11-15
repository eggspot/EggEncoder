using EggEncoder.Video;
using EggEncoder.Video.Filters;
using FluentAssertions;

namespace EggEncoder.UnitTests.Video.Filters
{
    public class ColorConvertTransformTest
    {
        public static TheoryData<VideoColorRange, VideoColorMatrix, VideoColorRange, VideoColorMatrix> Conversions => new()
        {
            { VideoColorRange.Limited, VideoColorMatrix.Bt601, VideoColorRange.Limited, VideoColorMatrix.Bt709 },
            { VideoColorRange.Full, VideoColorMatrix.Bt601, VideoColorRange.Limited, VideoColorMatrix.Bt709 },
            { VideoColorRange.Limited, VideoColorMatrix.Bt709, VideoColorRange.Full, VideoColorMatrix.Bt601 },
            { VideoColorRange.Full, VideoColorMatrix.Bt709, VideoColorRange.Full, VideoColorMatrix.Bt709 }
        };

        [Theory]
        [MemberData(nameof(Conversions))]
        public void Apply_Solid_Colour_Should_Keep_Its_Rgb(VideoColorRange fromRange, VideoColorMatrix fromMatrix, VideoColorRange toRange, VideoColorMatrix toMatrix)
        {
            var source = new VideoFramePool().Rent(7, 5, fromRange, fromMatrix);
            source.Fill(YuvColor.FromRgb(210, 90, 40, fromMatrix, fromRange));
            source.Timestamp = TimeSpan.FromSeconds(2);
            var expected = YuvColor.FromRgb(210, 90, 40, toMatrix, toRange);
            VideoFrame? output = null;

            new ColorConvertTransform(toRange, toMatrix).Apply(source, frame => output = frame);

            using (output)
            {
                output!.Range.Should().Be(toRange);
                output.Matrix.Should().Be(toMatrix);
                output.Timestamp.Should().Be(TimeSpan.FromSeconds(2));
                output.GetRow(VideoPlane.Y, 4).ToArray().Should().OnlyContain(v => Math.Abs(v - expected.Y) <= 1);
                output.GetRow(VideoPlane.U, 2).ToArray().Should().OnlyContain(v => Math.Abs(v - expected.U) <= 1);
                output.GetRow(VideoPlane.V, 0).ToArray().Should().OnlyContain(v => Math.Abs(v - expected.V) <= 1);
            }
        }

        [Fact]
        public void Apply_Frame_Already_In_Target_Space_Should_Pass_The_Same_Frame()
        {
            using var source = VideoFrame.Create(4, 4, VideoColorRange.Limited, VideoColorMatrix.Bt709);
            VideoFrame? output = null;

            new ColorConvertTransform(VideoColorRange.Limited, VideoColorMatrix.Bt709).Apply(source, frame => output = frame);

            output.Should().BeSameAs(source);
        }

        [Fact]
        public void Members_Should_Validate_And_Report_Size()
        {
            var transform = new ColorConvertTransform(VideoColorRange.Full, VideoColorMatrix.Bt601);

            transform.GetOutputSize(9, 3).Should().Be((9, 3));
            transform.CanChangeFrameCount.Should().BeFalse();
            ((Action)(() => _ = new ColorConvertTransform((VideoColorRange)7, VideoColorMatrix.Bt601))).Should().Throw<ArgumentOutOfRangeException>();
            ((Action)(() => _ = new ColorConvertTransform(VideoColorRange.Full, (VideoColorMatrix)7))).Should().Throw<ArgumentOutOfRangeException>();
            ((Action)(() => transform.Apply(null!, _ => { }))).Should().Throw<ArgumentNullException>();
            ((Action)(() => transform.Apply(VideoFrame.Create(2, 2), null!))).Should().Throw<ArgumentNullException>();
        }
    }
}
