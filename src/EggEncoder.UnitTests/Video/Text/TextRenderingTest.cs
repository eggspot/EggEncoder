using EggEncoder.Video;
using EggEncoder.Video.Text;
using FluentAssertions;
using static EggEncoder.UnitTests.Video.Text.TrueTypeFontTest;

namespace EggEncoder.UnitTests.Video.Text
{
    public class GlyphRasterizerTest
    {
        [Fact]
        public void A_Pixel_Aligned_Square_Should_Cover_Exactly_Its_Area()
        {
            var (mask, left, top) = GlyphRasterizer.Rasterize(Synthetic().GetOutline(1), 0.02, 0);

            (mask.Width, mask.Height, left, top).Should().Be((10, 10, 0, -10));
            mask.Coverage.ToArray().Should().OnlyContain(c => c == 255);
        }

        [Fact]
        public void A_Half_Pixel_Offset_Should_Half_Cover_The_Edge_Columns()
        {
            var (mask, left, _) = GlyphRasterizer.Rasterize(Synthetic().GetOutline(1), 0.02, 0.5);

            mask.Width.Should().Be(11);
            left.Should().Be(0);
            mask.Coverage[0].Should().Be(128);
            mask.Coverage[1].Should().Be(255);
            mask.Coverage[10].Should().Be(128);
            Sum(mask).Should().BeApproximately(100, 0.5);
        }

        [Fact]
        public void A_Curved_Glyph_Should_Cover_Its_Analytic_Area()
        {
            var (mask, _, _) = GlyphRasterizer.Rasterize(Synthetic().GetOutline(2), 0.02, 0);

            Sum(mask).Should().BeApproximately(208333.33 * 0.02 * 0.02, 83.33 * 0.01);
        }

        [Fact]
        public void Overlapping_Contours_Of_The_Same_Direction_Should_Not_Exceed_Full_Coverage()
        {
            var (mask, _, _) = GlyphRasterizer.Rasterize(Synthetic().GetOutline(3), 0.02, 0);

            mask.Coverage.ToArray().Should().OnlyContain(c => c <= 255);
            Sum(mask).Should().BeGreaterThan(100);
        }

        [Fact]
        public void An_Empty_Outline_Should_Give_An_Empty_Mask()
        {
            var (mask, _, _) = GlyphRasterizer.Rasterize(new GlyphOutline(), 1, 0);

            (mask.Width, mask.Height).Should().Be((0, 0));
        }

        internal static double Sum(AlphaMask mask) => mask.Coverage.ToArray().Sum(c => c / 255.0);
    }

    public class TextRendererTest
    {
        [Fact]
        public void MeasureLine_Should_Sum_Advances_And_Kerning()
        {
            var renderer = new TextRenderer(Noto());

            renderer.MeasureLine("AV", 1000).Should().Be(639 - 40 + 600);
            renderer.MeasureLine("AV", 50).Should().BeApproximately((639 - 40 + 600) * 0.05, 1e-9);
            renderer.MeasureLine("", 50).Should().Be(0);
        }

        [Fact]
        public void Render_Should_Wrap_At_Spaces_And_Break_Long_Words()
        {
            var renderer = new TextRenderer(Synthetic());
            var style = new TextStyle { FontSize = 1000 };

            renderer.Render("aa aa", style, 2000).LineCount.Should().Be(2);
            renderer.Render("aa aa", style).LineCount.Should().Be(1);
            renderer.Render("aaaaa", style, 2000).LineCount.Should().Be(2);
            renderer.Render("a\na\n\na", style).LineCount.Should().Be(4);
        }

        [Fact]
        public void Render_Should_Normalize_To_Nfc()
        {
            var renderer = new TextRenderer(Noto());
            var style = new TextStyle { FontSize = 40 };

            var precomposed = renderer.Render("Việt", style);
            var decomposed = renderer.Render("Việt", style);

            decomposed.Width.Should().Be(precomposed.Width);
            decomposed.Fill.Coverage.ToArray().Should().Equal(precomposed.Fill.Coverage.ToArray());
        }

        [Fact]
        public void Render_Should_Place_Lines_On_Baselines_And_Size_The_Box()
        {
            var renderer = new TextRenderer(Synthetic());

            var text = renderer.Render("a\na", new TextStyle { FontSize = 100 });

            text.Width.Should().Be(60);
            text.TextBounds.Should().Be(new VideoRectangle(0, 0, 60, 218));
            GlyphRasterizerTest.Sum(text.Fill).Should().BeApproximately(2 * 50 * 50, 1);
            text.Fill.Coverage[(30 * 60) + 10].Should().Be(255);
            text.Fill.Coverage[(85 * 60) + 10].Should().Be(0);
            text.Fill.Coverage[(140 * 60) + 10].Should().Be(255);
        }

        [Fact]
        public void Alignment_Should_Move_Shorter_Lines()
        {
            var renderer = new TextRenderer(Synthetic());

            var right = renderer.Render("aa\na", new TextStyle { FontSize = 100, Alignment = TextAlignment.Right });
            var center = renderer.Render("aa\na", new TextStyle { FontSize = 100, Alignment = TextAlignment.Center });

            right.Fill.Coverage[(140 * right.Width) + 5].Should().Be(0);
            right.Fill.Coverage[(140 * right.Width) + 65].Should().Be(255);
            center.Fill.Coverage[(140 * center.Width) + 35].Should().Be(255);
            center.Fill.Coverage[(140 * center.Width) + 25].Should().Be(0);
        }

        [Fact]
        public void A_Missing_Character_Should_Draw_The_Notdef_Box()
        {
            var renderer = new TextRenderer(Synthetic());

            var text = renderer.Render("z", new TextStyle { FontSize = 100 });

            GlyphRasterizerTest.Sum(text.Fill).Should().BeApproximately(40 * 40, 1);
        }

        [Fact]
        public void Outline_Shadow_And_Background_Should_Grow_The_Canvas()
        {
            var renderer = new TextRenderer(Synthetic());
            var style = new TextStyle
            {
                FontSize = 100,
                OutlineWidth = 3,
                ShadowColor = new RgbaColor(0, 0, 0, 128),
                ShadowOffsetX = 5,
                ShadowOffsetY = 4,
                BackgroundColor = new RgbaColor(0, 0, 255, 200),
                BackgroundPadding = 6
            };

            var text = renderer.Render("a", style);

            text.TextBounds.Should().Be(new VideoRectangle(6, 6, 60, 109));
            (text.Width, text.Height).Should().Be((60 + 6 + 8, 109 + 6 + 7));
            text.Background.Should().Be(new VideoRectangle(0, 0, 72, 121));
            text.Outline!.Coverage[(37 * text.Width) + 4].Should().Be(255);
            text.Fill.Coverage[(37 * text.Width) + 4].Should().Be(0);
            text.Shadow!.Coverage[(45 * text.Width) + 9].Should().Be(255);
            text.Shadow.Coverage[(45 * text.Width) + 3].Should().Be(0);
        }

        [Fact]
        public void Blank_Text_Should_Render_Empty_And_Draw_Nothing()
        {
            var renderer = new TextRenderer(Synthetic());

            var text = renderer.Render("  \n ", new TextStyle());
            using var frame = VideoFrame.Create(4, 4);
            frame.Fill(new YuvColor(16, 128, 128));
            text.DrawOn(frame, 0, 0);

            (text.Width, text.Height, text.LineCount).Should().Be((0, 0, 0));
            frame.GetRow(VideoPlane.Y, 0)[0].Should().Be(16);
        }

        [Fact]
        public void DrawOn_Should_Paint_Background_Outline_And_Fill_Clipped()
        {
            var renderer = new TextRenderer(Synthetic());
            var text = renderer.Render("a", new TextStyle { FontSize = 20, OutlineWidth = 1, BackgroundColor = new RgbaColor(255, 0, 0), BackgroundPadding = 2 });
            using var frame = VideoFrame.Create(20, 20);
            frame.Fill(new YuvColor(16, 128, 128));

            text.DrawOn(frame, 3, -4);
            text.DrawOn(frame, 100, 100);

            var red = new RgbaColor(255, 0, 0).ToYuv(VideoColorMatrix.Bt709, VideoColorRange.Limited);
            frame.GetRow(VideoPlane.Y, 0)[3].Should().Be(red.Y);
            frame.GetRow(VideoPlane.Y, 5)[6].Should().Be(235);
            frame.GetRow(VideoPlane.Y, 5)[2].Should().Be(16);
            frame.GetRow(VideoPlane.Y, 19)[19].Should().Be(16);
            FluentActions.Invoking(() => text.DrawOn(null!, 0, 0)).Should().Throw<ArgumentNullException>();
        }

        [Theory]
        [InlineData(TextAnchor.TopLeft, 10, 5)]
        [InlineData(TextAnchor.TopCenter, 70, 5)]
        [InlineData(TextAnchor.TopRight, 130, 5)]
        [InlineData(TextAnchor.Center, 70, 45)]
        [InlineData(TextAnchor.BottomLeft, 10, 86)]
        [InlineData(TextAnchor.BottomRight, 130, 86)]
        [InlineData(TextAnchor.MiddleRight, 130, 45)]
        public void Place_Should_Anchor_The_Canvas_With_Margins(TextAnchor anchor, int x, int y)
        {
            var text = new TextRenderer(Synthetic()).Render("a", new TextStyle { FontSize = 100 });

            text.Place(anchor, 200, 200, 10, 5).Should().Be((x, y));
        }

        [Fact]
        public void Invalid_Arguments_Should_Throw()
        {
            var renderer = new TextRenderer(Synthetic());

            FluentActions.Invoking(() => new TextRenderer(null!)).Should().Throw<ArgumentNullException>();
            renderer.Invoking(r => r.Render(null!, new TextStyle())).Should().Throw<ArgumentNullException>();
            renderer.Invoking(r => r.Render("a", null!)).Should().Throw<ArgumentNullException>();
            renderer.Invoking(r => r.Render("a", new TextStyle(), -1)).Should().Throw<ArgumentOutOfRangeException>();
            renderer.Invoking(r => r.MeasureLine(null!, 10)).Should().Throw<ArgumentNullException>();
            renderer.Font.GlyphCount.Should().Be(5);
        }

        public static TheoryData<TextStyle, string> InvalidStyles => new()
        {
            { new() { FontSize = 0 }, "FontSize" },
            { new() { OutlineWidth = 65 }, "OutlineWidth" },
            { new() { ShadowOffsetX = 300 }, "ShadowOffsetX" },
            { new() { ShadowOffsetY = -300 }, "ShadowOffsetY" },
            { new() { BackgroundPadding = -1 }, "BackgroundPadding" },
            { new() { LineSpacing = 5 }, "LineSpacing" },
            { new() { Alignment = (TextAlignment)7 }, "Alignment" }
        };

        [Theory]
        [MemberData(nameof(InvalidStyles))]
        public void Style_Validate_Should_Name_The_Property(TextStyle style, string property)
        {
            style.Invoking(s => s.Validate()).Should().Throw<ArgumentOutOfRangeException>().WithParameterName(property);
        }
    }

    public class TextOverlayTransformTest
    {
        [Fact]
        public void Overlay_Should_Draw_Inside_Its_Window_At_Its_Anchor()
        {
            var transform = new TextOverlayTransform(new TextRenderer(Synthetic()), "a", new TextStyle { FontSize = 20 }, TextAnchor.TopLeft, 2, 3, start: TimeSpan.FromSeconds(1), end: TimeSpan.FromSeconds(2));

            using var before = Run(transform, TimeSpan.Zero);
            using var during = Run(transform, TimeSpan.FromSeconds(1.5));
            using var after = Run(transform, TimeSpan.FromSeconds(2));

            before.GetRow(VideoPlane.Y, 12)[5].Should().Be(16);
            during.GetRow(VideoPlane.Y, 12)[5].Should().Be(235);
            after.GetRow(VideoPlane.Y, 12)[5].Should().Be(16);
            transform.GetOutputSize(3, 4).Should().Be((3, 4));
            transform.CanChangeFrameCount.Should().BeFalse();
        }

        [Fact]
        public void Overlay_Should_Wrap_To_The_Frame_Width_And_Rerender_When_It_Changes()
        {
            var transform = new TextOverlayTransform(new TextRenderer(Synthetic()), "aa aa", new TextStyle { FontSize = 20 }, TextAnchor.TopLeft);

            using var narrow = Run(transform, TimeSpan.Zero, 30, 60);
            using var wide = Run(transform, TimeSpan.Zero, 80, 60);

            narrow.GetRow(VideoPlane.Y, 30)[2].Should().Be(235);
            wide.GetRow(VideoPlane.Y, 30)[2].Should().Be(16);
            wide.GetRow(VideoPlane.Y, 10)[40].Should().Be(235);
        }

        [Fact]
        public void Invalid_Arguments_Should_Throw()
        {
            var renderer = new TextRenderer(Synthetic());

            FluentActions.Invoking(() => new TextOverlayTransform(null!, "a", new TextStyle())).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => new TextOverlayTransform(renderer, null!, new TextStyle())).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => new TextOverlayTransform(renderer, "a", null!)).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => new TextOverlayTransform(renderer, "a", new TextStyle { FontSize = -1 })).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => new TextOverlayTransform(renderer, "a", new TextStyle(), (TextAnchor)20)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => new TextOverlayTransform(renderer, "a", new TextStyle(), marginX: -1)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => new TextOverlayTransform(renderer, "a", new TextStyle(), marginY: -1)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => new TextOverlayTransform(renderer, "a", new TextStyle(), maxWidth: -1)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => new TextOverlayTransform(renderer, "a", new TextStyle(), start: TimeSpan.FromSeconds(2), end: TimeSpan.FromSeconds(1))).Should().Throw<ArgumentException>();
        }

        private static VideoFrame Run(IVideoFrameTransform transform, TimeSpan timestamp, int width = 40, int height = 40)
        {
            var frame = VideoFrame.Create(width, height);
            frame.Fill(new YuvColor(16, 128, 128));
            frame.Timestamp = timestamp;
            VideoFrame? output = null;
            transform.Apply(frame, f => output = f);
            return output!;
        }
    }
}
