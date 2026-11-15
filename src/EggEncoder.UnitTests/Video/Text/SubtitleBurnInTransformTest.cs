using EggEncoder.Video;
using EggEncoder.Video.Text;
using FluentAssertions;
using static EggEncoder.UnitTests.Video.Text.TrueTypeFontTest;

namespace EggEncoder.UnitTests.Video.Text
{
    public class SubtitleBurnInTransformTest
    {
        private static readonly SubtitleStyle Big = new() { FontSize = 60 };

        [Theory]
        [InlineData(2, 185, 250, 185, 200)]
        [InlineData(8, 185, 40, 185, 100)]
        [InlineData(1, 20, 250, 60, 250)]
        [InlineData(3, 355, 250, 320, 250)]
        [InlineData(5, 185, 140, 185, 100)]
        public void Alignment_Should_Place_The_Subtitle_With_Its_Margins(int alignment, int litX, int litY, int darkX, int darkY)
        {
            var transform = Transform([new SubtitleCue(TimeSpan.Zero, TimeSpan.FromSeconds(1), "a")], Big with { Alignment = alignment });

            using var frame = Run(transform, TimeSpan.Zero, 384, 288);

            frame.GetRow(VideoPlane.Y, litY)[litX].Should().Be(235);
            frame.GetRow(VideoPlane.Y, darkY)[darkX].Should().Be(128);
        }

        [Fact]
        public void Sizes_Should_Scale_With_The_Frame_Height()
        {
            var transform = Transform([new SubtitleCue(TimeSpan.Zero, TimeSpan.FromSeconds(1), "a")], Big);

            using var frame = Run(transform, TimeSpan.Zero, 384, 576);

            frame.GetRow(VideoPlane.Y, 500)[185].Should().Be(235);
            frame.GetRow(VideoPlane.Y, 476)[205].Should().Be(235);
            frame.GetRow(VideoPlane.Y, 470)[185].Should().NotBe(235);
        }

        [Fact]
        public void Overlapping_Cues_Should_Stack_Upwards_From_The_Bottom()
        {
            var transform = Transform(
            [
                new SubtitleCue(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), "a"),
                new SubtitleCue(TimeSpan.Zero, TimeSpan.FromSeconds(3), "a")
            ], Big);

            using var frame = Run(transform, TimeSpan.FromSeconds(2), 384, 288);

            frame.GetRow(VideoPlane.Y, 250)[185].Should().Be(235);
            frame.GetRow(VideoPlane.Y, 192)[185].Should().Be(235);
            frame.GetRow(VideoPlane.Y, 225)[185].Should().Be(128);
        }

        [Fact]
        public void Cues_Should_Show_From_Start_Inclusive_To_End_Exclusive()
        {
            var transform = Transform([new SubtitleCue(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "a")], Big);

            using var before = Run(transform, TimeSpan.FromSeconds(0.99), 384, 288);
            using var start = Run(transform, TimeSpan.FromSeconds(1), 384, 288);
            using var end = Run(transform, TimeSpan.FromSeconds(2), 384, 288);

            before.GetRow(VideoPlane.Y, 250)[185].Should().Be(128);
            start.GetRow(VideoPlane.Y, 250)[185].Should().Be(235);
            end.GetRow(VideoPlane.Y, 250)[185].Should().Be(128);
        }

        [Fact]
        public void BorderStyle_3_Should_Draw_An_Opaque_Box_In_The_Outline_Colour()
        {
            var transform = Transform([new SubtitleCue(TimeSpan.Zero, TimeSpan.FromSeconds(1), "a")], Big with { BorderStyle = 3, Outline = 4 });

            using var frame = Run(transform, TimeSpan.Zero, 384, 288);

            frame.GetRow(VideoPlane.Y, 270)[175].Should().Be(16);
            frame.GetRow(VideoPlane.Y, 250)[185].Should().Be(235);
        }

        [Fact]
        public void Line_Breaks_And_Tags_Should_Be_Handled_Like_Ass()
        {
            SubtitleBurnInTransform.PlainText("{\\b1}Hello{\\b0}\\Nworld\\hnow\\nhere").Should().Be("Hello\nworld\u00A0now here");

            var transform = Transform([new SubtitleCue(TimeSpan.Zero, TimeSpan.FromSeconds(1), "a\\Na")], Big);
            using var frame = Run(transform, TimeSpan.Zero, 384, 288);

            frame.GetRow(VideoPlane.Y, 250)[185].Should().Be(235);
            frame.GetRow(VideoPlane.Y, 196)[185].Should().Be(235);
        }

        [Fact]
        public void Default_Style_Should_Follow_Ffmpegs_Srt_Defaults()
        {
            var style = new SubtitleStyle();

            (style.FontSize, style.Outline, style.Shadow, style.MarginL, style.MarginR, style.MarginV, style.Alignment, style.BorderStyle, style.PlayResX, style.PlayResY)
                .Should().Be((16.0, 1.0, 0.0, 10, 10, 10, 2, 1, 384, 288));
            using var frame = Run(Transform([new SubtitleCue(TimeSpan.Zero, TimeSpan.FromSeconds(1), "  ")], null), TimeSpan.Zero, 64, 64);
            frame.GetRow(VideoPlane.Y, 60)[32].Should().Be(128);
        }

        [Fact]
        public void A_New_Frame_Size_Should_Render_Again()
        {
            var transform = Transform([new SubtitleCue(TimeSpan.Zero, TimeSpan.FromSeconds(1), "a")], Big);

            using var small = Run(transform, TimeSpan.Zero, 384, 288);
            using var large = Run(transform, TimeSpan.Zero, 384, 576);

            small.GetRow(VideoPlane.Y, 250)[185].Should().Be(235);
            large.GetRow(VideoPlane.Y, 500)[185].Should().Be(235);
            transform.GetOutputSize(5, 6).Should().Be((5, 6));
            transform.CanChangeFrameCount.Should().BeFalse();
        }

        [Fact]
        public void Invalid_Arguments_Should_Throw()
        {
            var renderer = new TextRenderer(Synthetic());

            FluentActions.Invoking(() => new SubtitleBurnInTransform(null!, [])).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => new SubtitleBurnInTransform(renderer, null!)).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => new SubtitleBurnInTransform(renderer, [null!])).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => new SubtitleBurnInTransform(renderer, [new SubtitleCue(TimeSpan.Zero, TimeSpan.Zero, null!)])).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => new SubtitleBurnInTransform(renderer, [new SubtitleCue(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), "a")])).Should().Throw<ArgumentException>();
            FluentActions.Invoking(() => SubtitleBurnInTransform.PlainText(null!)).Should().Throw<ArgumentNullException>();
        }

        public static TheoryData<SubtitleStyle, string> InvalidStyles => new()
        {
            { new() { FontSize = 0 }, "FontSize" },
            { new() { Outline = 17 }, "Outline" },
            { new() { Shadow = -1 }, "Shadow" },
            { new() { MarginL = -1 }, "MarginL" },
            { new() { MarginR = -1 }, "MarginR" },
            { new() { MarginV = -1 }, "MarginV" },
            { new() { Alignment = 0 }, "Alignment" },
            { new() { BorderStyle = 2 }, "BorderStyle" },
            { new() { PlayResX = 0 }, "PlayResX" },
            { new() { PlayResY = 0 }, "PlayResY" }
        };

        [Theory]
        [MemberData(nameof(InvalidStyles))]
        public void Style_Validate_Should_Name_The_Property(SubtitleStyle style, string property)
        {
            style.Invoking(s => s.Validate()).Should().Throw<ArgumentOutOfRangeException>().WithParameterName(property);
        }

        private static SubtitleBurnInTransform Transform(SubtitleCue[] cues, SubtitleStyle? style) =>
            new(new TextRenderer(Synthetic()), cues, style);

        private static VideoFrame Run(IVideoFrameTransform transform, TimeSpan timestamp, int width, int height)
        {
            var frame = VideoFrame.Create(width, height);
            frame.Fill(new YuvColor(128, 128, 128));
            frame.Timestamp = timestamp;
            VideoFrame? output = null;
            transform.Apply(frame, f => output = f);
            return output!;
        }
    }
}
