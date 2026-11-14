using EggEncoder.Video.Text;
using FluentAssertions;

namespace EggEncoder.UnitTests.Video.Text
{
    public class TrueTypeFontTest
    {
        internal static byte[] Bytes(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Video", "Text", name));

        internal static TrueTypeFont Noto() => TrueTypeFont.Load(Bytes("NotoSans-subset.ttf"));

        internal static TrueTypeFont Synthetic() => TrueTypeFont.Load(Bytes("synthetic.ttf"));

        [Fact]
        public void Load_Should_Read_The_Metrics()
        {
            var noto = Noto();
            var synthetic = Synthetic();

            (noto.UnitsPerEm, noto.Ascender, noto.Descender, noto.LineGap, noto.WinAscent, noto.WinDescent, noto.GlyphCount)
                .Should().Be((1000, 1069, -293, 0, 1124, 395, 304));
            (synthetic.UnitsPerEm, synthetic.Ascender, synthetic.Descender, synthetic.LineGap, synthetic.WinAscent, synthetic.WinDescent, synthetic.GlyphCount)
                .Should().Be((1000, 800, -200, 90, 900, 300, 5));
        }

        [Theory]
        [InlineData('A', 34, 639)]
        [InlineData('V', 55, 600)]
        [InlineData('e', 70, 564)]
        [InlineData('ệ', 249, 564)]
        [InlineData(' ', 1, 260)]
        [InlineData('W', 56, 930)]
        public void Noto_Should_Map_Characters_To_Glyphs_And_Advances(char character, int glyph, int advance)
        {
            var font = Noto();

            font.GetGlyphIndex(character).Should().Be(glyph);
            font.GetAdvanceWidth(glyph).Should().Be(advance);
        }

        [Fact]
        public void Synthetic_Should_Map_Through_Format_4_And_Format_12()
        {
            var font = Synthetic();

            font.GetGlyphIndex('a').Should().Be(1);
            font.GetGlyphIndex('d').Should().Be(4);
            font.GetGlyphIndex(0x1F600).Should().Be(1);
            font.GetGlyphIndex('z').Should().Be(0);
            font.GetGlyphIndex(0x10FFFF).Should().Be(0);
            Enumerable.Range(0, 5).Select(font.GetAdvanceWidth).Should().Equal(500, 600, 550, 700, 900);
        }

        [Theory]
        [InlineData('A', 'V', -40)]
        [InlineData('V', 'A', -40)]
        [InlineData('T', 'o', -70)]
        [InlineData('W', 'a', -20)]
        [InlineData('L', 'T', -20)]
        [InlineData('A', 'B', 0)]
        public void Noto_Should_Kern_From_Gpos(char left, char right, int expected)
        {
            var font = Noto();

            font.GetKerning(font.GetGlyphIndex(left), font.GetGlyphIndex(right)).Should().Be(expected);
        }

        [Fact]
        public void Synthetic_Should_Kern_From_The_Kern_Table()
        {
            var font = Synthetic();

            font.GetKerning(1, 2).Should().Be(-100);
            font.GetKerning(2, 1).Should().Be(40);
            font.GetKerning(1, 1).Should().Be(0);
        }

        [Fact]
        public void Gpos_Through_Extension_Lookups_Should_Add_Pair_And_Class_Adjustments()
        {
            var font = TrueTypeFont.Load(Bytes("synthetic-gpos.ttf"));

            font.GetKerning(1, 2).Should().Be(-75);
            font.GetKerning(2, 1).Should().Be(25);
            font.GetKerning(3, 4).Should().Be(-15);
            font.GetKerning(1, 1).Should().Be(0);
            font.GetKerning(4, 4).Should().Be(0);
        }

        [Fact]
        public void Simple_Glyphs_Should_Keep_Their_Points_And_Curve_Flags()
        {
            var font = Synthetic();

            Points(font.GetOutline(1)).Should().Equal((0, 0, true), (0, 500, true), (500, 500, true), (500, 0, true));
            Points(font.GetOutline(2)).Should().Equal((0, 0, true), (0, 500, false), (500, 500, false), (500, 0, true));
            font.GetOutline(2).ContourEnds.Should().Equal(3);
        }

        [Fact]
        public void Composite_Glyphs_Should_Apply_Offsets_Scales_And_Two_By_Two_Transforms_Through_Nesting()
        {
            var font = Synthetic();

            Points(font.GetOutline(3)).Should().Equal(
                (100, 200, true), (100, 700, true), (600, 700, true), (600, 200, true),
                (0, 0, true), (0, 250, false), (250, 250, false), (250, 0, true));
            Points(font.GetOutline(4)).Should().Equal(
                (50, 200, true), (50, 700, true), (550, 700, true), (550, 200, true),
                (-50, 0, true), (-50, 250, false), (200, 250, false), (200, 0, true),
                (900, 0, true), (400, 0, true), (400, 500, true), (900, 500, true));
            font.GetOutline(4).ContourEnds.Should().Equal(3, 7, 11);
        }

        [Fact]
        public void Vietnamese_Composite_Should_Stack_Its_Marks_Within_The_Expected_Bounds()
        {
            var outline = Noto().GetOutline(249);

            outline.Bounds().Should().Be((55, -176, 513, 766));
            outline.ContourEnds.Count.Should().BeGreaterThan(3);
        }

        [Fact]
        public void Empty_Glyph_Should_Have_No_Points()
        {
            Noto().GetOutline(1).PointCount.Should().Be(0);
        }

        [Fact]
        public void Glyph_Outside_The_Font_Should_Throw()
        {
            var font = Synthetic();

            font.Invoking(f => f.GetAdvanceWidth(5)).Should().Throw<ArgumentOutOfRangeException>();
            font.Invoking(f => f.GetOutline(-1)).Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Load_Should_Copy_The_Bytes()
        {
            var bytes = Bytes("synthetic.ttf");
            var font = TrueTypeFont.Load(bytes);

            Array.Clear(bytes);

            font.GetGlyphIndex('a').Should().Be(1);
            font.GetOutline(1).PointCount.Should().Be(4);
        }

        [Theory]
        [InlineData("OTTO")]
        [InlineData("ttcf")]
        public void Cff_Fonts_And_Collections_Should_Throw_NotSupported(string tag)
        {
            var bytes = new byte[64];
            System.Text.Encoding.ASCII.GetBytes(tag).CopyTo(bytes, 0);

            FluentActions.Invoking(() => TrueTypeFont.Load(bytes)).Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void Not_A_Font_Should_Throw_InvalidData()
        {
            FluentActions.Invoking(() => TrueTypeFont.Load(new byte[3])).Should().Throw<InvalidDataException>();
            FluentActions.Invoking(() => TrueTypeFont.Load("RIFF0000WAVEfmt "u8)).Should().Throw<InvalidDataException>();
        }

        [Fact]
        public void Truncated_Fonts_Should_Throw_InvalidData_And_Nothing_Else()
        {
            var bytes = Bytes("NotoSans-subset.ttf");
            for (var length = 0; length < bytes.Length; length += 97)
            {
                var truncated = bytes.AsSpan(0, length).ToArray();
                Exercise(truncated);
            }
        }

        [Fact]
        public void Garbled_Fonts_Should_Throw_InvalidData_And_Nothing_Else()
        {
            var original = Bytes("synthetic.ttf");
            var random = new Random(1234);
            for (var round = 0; round < 3000; round++)
            {
                var bytes = (byte[])original.Clone();
                for (var flips = 0; flips < 4; flips++)
                {
                    bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                }

                Exercise(bytes);
            }
        }

        private static void Exercise(byte[] bytes)
        {
            try
            {
                var font = TrueTypeFont.Load(bytes);
                var renderer = new TextRenderer(font);
                for (var glyph = 0; glyph < Math.Min(font.GlyphCount, 40); glyph++)
                {
                    font.GetAdvanceWidth(glyph);
                    font.GetKerning(glyph, (glyph + 1) % font.GlyphCount);
                    var outline = font.GetOutline(glyph);
                    var bounds = outline.Bounds();
                    if (Math.Max(bounds.XMax - bounds.XMin, bounds.YMax - bounds.YMin) < 70000)
                    {
                        GlyphRasterizer.Rasterize(outline, 0.01, 0);
                    }
                }

                renderer.Render("abcd AV ệ", new TextStyle { FontSize = 6 });
            }
            catch (InvalidDataException)
            {
            }
        }

        private static List<(int, int, bool)> Points(GlyphOutline outline) =>
            Enumerable.Range(0, outline.PointCount).Select(i => ((int)Math.Round(outline.X(i)), (int)Math.Round(outline.Y(i)), outline.IsOnCurve(i))).ToList();
    }
}
