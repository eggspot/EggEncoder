using EggEncoder.Video;
using EggEncoder.Video.Filters;
using FluentAssertions;
using static EggEncoder.UnitTests.Video.Filters.FrameTestSupport;

namespace EggEncoder.UnitTests.Video.Filters
{
    public class FrameRateTransformTest
    {
        [Fact]
        public void From_25_To_30_Should_Duplicate_Every_Fifth_Frame()
        {
            var output = Convert(new VideoFrameRate(30), Enumerable.Range(0, 25).Select(n => (n * 40.0, 40.0)).ToArray());

            output.Should().HaveCount(30);
            output.Select(o => o.Source).Take(7).Should().Equal(0, 1, 2, 2, 3, 4, 5);
            output[29].Timestamp.Should().Be(new VideoFrameRate(30).TimestampOf(29));
            output.Should().OnlyContain(o => o.Duration == new VideoFrameRate(30).FrameDuration);
        }

        [Fact]
        public void From_30_To_25_Should_Drop_Frames_Nearest_In_Time()
        {
            var output = Convert(new VideoFrameRate(25), Enumerable.Range(0, 30).Select(n => (n * 1000.0 / 30, 0.0)).ToArray());

            output.Should().HaveCount(25);
            output.Select(o => o.Source).Take(6).Should().Equal(0, 1, 2, 4, 5, 6);
        }

        [Fact]
        public void Ntsc_Timestamps_Should_Be_Computed_From_The_Index()
        {
            var rate = new VideoFrameRate(30000, 1001);
            var output = Convert(rate, Enumerable.Range(0, 300).Select(n => (n * 1001.0 / 30, 0.0)).ToArray(), startMs: 500);

            output.Should().HaveCount(300);
            output.Select(o => o.Source).Should().Equal(Enumerable.Range(0, 300).Select(n => n % 256));
            output[299].Timestamp.Should().Be(TimeSpan.FromMilliseconds(500) + rate.TimestampOf(299));
        }

        [Fact]
        public void Variable_Rate_Input_Should_Fill_Gaps_With_The_Previous_Frame()
        {
            var output = Convert(new VideoFrameRate(10), [(0, 0), (100, 0), (450, 120)]);

            output.Select(o => o.Source).Should().Equal(0, 1, 1, 1, 1, 2);
        }

        [Fact]
        public void A_Single_Frame_Should_Be_Emitted_Once_On_Flush()
        {
            var output = Convert(new VideoFrameRate(30), [(1000, 0)]);

            output.Should().ContainSingle();
            output[0].Timestamp.Should().Be(TimeSpan.FromSeconds(1));
        }

        [Fact]
        public void Flush_Without_Frames_Should_Emit_Nothing_And_Invalid_Rate_Should_Throw()
        {
            var transform = new FrameRateTransform(new VideoFrameRate(25));
            transform.Flush(_ => throw new InvalidOperationException());

            transform.CanChangeFrameCount.Should().BeTrue();
            transform.GetOutputSize(3, 4).Should().Be((3, 4));
            FluentActions.Invoking(() => new FrameRateTransform(default)).Should().Throw<ArgumentOutOfRangeException>();
            transform.Invoking(t => t.Apply(null!, _ => { })).Should().Throw<ArgumentNullException>();
            transform.Invoking(t => t.Flush(null!)).Should().Throw<ArgumentNullException>();
        }

        private static List<(int Source, TimeSpan Timestamp, TimeSpan Duration)> Convert(VideoFrameRate rate, (double Ms, double DurationMs)[] inputs, double startMs = 0)
        {
            var transform = new FrameRateTransform(rate);
            var output = new List<(int, TimeSpan, TimeSpan)>();
            void Collect(VideoFrame frame)
            {
                output.Add((frame.GetRow(VideoPlane.Y, 0)[0], frame.Timestamp, frame.Duration));
                frame.Dispose();
            }

            for (var i = 0; i < inputs.Length; i++)
            {
                var frame = VideoFrame.Create(2, 2);
                frame.GetRow(VideoPlane.Y, 0)[0] = (byte)i;
                frame.Timestamp = TimeSpan.FromMilliseconds(startMs + inputs[i].Ms);
                frame.Duration = TimeSpan.FromMilliseconds(inputs[i].DurationMs);
                transform.Apply(frame, Collect);
            }

            transform.Flush(Collect);
            return output;
        }
    }

    public class BoxBlurTransformTest
    {
        [Fact]
        public void Blur_Should_Stay_Close_To_Ffmpeg_Boxblur()
        {
            using var expected = Load("ffmpeg_boxblur_2_1.yuv", 64, 48);

            using var actual = RunOne(new BoxBlurTransform(2, 1), Input());

            Psnr(actual, expected, VideoPlane.Y).Should().BeGreaterThan(40);
            Psnr(actual, expected, VideoPlane.U).Should().BeGreaterThan(40);
            Psnr(actual, expected, VideoPlane.V).Should().BeGreaterThan(40);
        }

        [Fact]
        public void Blur_Of_A_Flat_Frame_Should_Stay_Flat()
        {
            using var output = RunOne(new BoxBlurTransform(20, 10, 3), Solid(33, 17, new YuvColor(201, 33, 99)));

            output.GetRow(VideoPlane.Y, 16).ToArray().Should().OnlyContain(b => b == 201);
            output.GetRow(VideoPlane.U, 0).ToArray().Should().OnlyContain(b => b == 33);
            output.GetRow(VideoPlane.V, 8).ToArray().Should().OnlyContain(b => b == 99);
        }

        [Fact]
        public void An_Impulse_Should_Spread_Evenly_Over_The_Box()
        {
            using var input = Pattern(9, 9, (plane, x, y) => (byte)(plane == VideoPlane.Y && x == 4 && y == 4 ? 225 : plane == VideoPlane.Y ? 0 : 128));

            using var output = RunOne(new BoxBlurTransform(1, 0), input.Clone());

            for (var y = 0; y < 9; y++)
            {
                for (var x = 0; x < 9; x++)
                {
                    output.GetRow(VideoPlane.Y, y)[x].Should().Be((byte)(Math.Abs(x - 4) <= 1 && Math.Abs(y - 4) <= 1 ? 25 : 0));
                }
            }
        }

        [Fact]
        public void A_Region_Should_Leave_The_Rest_Alone_And_Threads_Should_Not_Change_The_Result()
        {
            using var serial = RunOne(new BoxBlurTransform(3, 2, 2, new VideoRectangle(9, 7, 20, 21)), Input());
            using var parallel = RunOne(new BoxBlurTransform(3, 2, 2, new VideoRectangle(9, 7, 20, 21), maxDegreeOfParallelism: 4), Input());
            using var input = Input();

            SamePixels(parallel, serial).Should().BeTrue();
            serial.GetRow(VideoPlane.Y, 5).ToArray().Should().Equal(input.GetRow(VideoPlane.Y, 5).ToArray());
            serial.GetRow(VideoPlane.Y, 28).ToArray().Should().Equal(input.GetRow(VideoPlane.Y, 28).ToArray());
            serial.GetRow(VideoPlane.Y, 15)[7].Should().Be(input.GetRow(VideoPlane.Y, 15)[7]);
            serial.GetRow(VideoPlane.Y, 15)[30].Should().Be(input.GetRow(VideoPlane.Y, 15)[30]);
            serial.GetRow(VideoPlane.Y, 15)[20].Should().NotBe(input.GetRow(VideoPlane.Y, 15)[20]);
        }

        [Fact]
        public void A_Region_Outside_The_Frame_Or_Radius_Zero_Should_Change_Nothing()
        {
            using var input = Input();

            using var outside = RunOne(new BoxBlurTransform(5, 5, region: new VideoRectangle(100, 100, 10, 10)), input.Clone());
            using var zero = RunOne(new BoxBlurTransform(0, 0), input.Clone());

            SamePixels(outside, input).Should().BeTrue();
            SamePixels(zero, input).Should().BeTrue();
        }

        [Fact]
        public void Invalid_Arguments_Should_Throw()
        {
            FluentActions.Invoking(() => new BoxBlurTransform(-1, 0)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => new BoxBlurTransform(0, -1)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => new BoxBlurTransform(1, 1, 0)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => new BoxBlurTransform(1, 1, 11)).Should().Throw<ArgumentOutOfRangeException>();
            new BoxBlurTransform(1, 1).GetOutputSize(7, 9).Should().Be((7, 9));
        }
    }

    public class DrawingTest
    {
        private static readonly YuvColor Black = new(16, 128, 128);

        [Fact]
        public void FillRectangle_Opaque_Should_Paint_Luma_And_Covered_Chroma()
        {
            using var frame = Solid(8, 8, Black);
            var white = new RgbaColor(255, 255, 255);

            VideoDraw.FillRectangle(frame, new VideoRectangle(2, 2, 4, 4), white);

            frame.GetRow(VideoPlane.Y, 2).ToArray().Should().Equal(16, 16, 235, 235, 235, 235, 16, 16);
            frame.GetRow(VideoPlane.Y, 1).ToArray().Should().OnlyContain(b => b == 16);
            frame.GetRow(VideoPlane.U, 1).ToArray().Should().Equal(128, 128, 128, 128);
        }

        [Fact]
        public void FillRectangle_At_An_Odd_Position_Should_Blend_Chroma_By_Coverage()
        {
            using var frame = Solid(4, 4, Black);

            VideoDraw.FillRectangle(frame, new VideoRectangle(1, 1, 2, 2), new RgbaColor(255, 0, 0));

            var red = new RgbaColor(255, 0, 0).ToYuv(VideoColorMatrix.Bt709, VideoColorRange.Limited);
            frame.GetRow(VideoPlane.Y, 1).ToArray().Should().Equal(16, red.Y, red.Y, 16);
            frame.GetRow(VideoPlane.V, 0)[0].Should().Be((byte)Math.Round(((128 * 3) + red.V) / 4.0));
        }

        [Fact]
        public void FillRectangle_HalfTransparent_Should_Blend_Halfway_And_Clip()
        {
            using var frame = Solid(4, 4, Black);

            VideoDraw.FillRectangle(frame, new VideoRectangle(-5, -5, 100, 100), new RgbaColor(255, 255, 255, 128));
            VideoDraw.FillRectangle(frame, new VideoRectangle(0, 0, 4, 4), new RgbaColor(0, 0, 0, 0));
            VideoDraw.FillRectangle(frame, new VideoRectangle(10, 10, 4, 4), new RgbaColor(0, 0, 0));

            frame.GetRow(VideoPlane.Y, 3).ToArray().Should().OnlyContain(b => b == 126);
            FluentActions.Invoking(() => VideoDraw.FillRectangle(null!, default, default)).Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void BlendMask_Should_Scale_Coverage_By_The_Colour_Alpha()
        {
            using var frame = Solid(4, 2, Black);
            var mask = new AlphaMask(2, 1, [255, 0]);

            VideoDraw.BlendMask(frame, mask, 1, 0, new RgbaColor(255, 255, 255));

            frame.GetRow(VideoPlane.Y, 0).ToArray().Should().Equal(16, 235, 16, 16);
            FluentActions.Invoking(() => VideoDraw.BlendMask(frame, null!, 0, 0, default)).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => VideoDraw.BlendMask(null!, mask, 0, 0, default)).Should().Throw<ArgumentNullException>();
            VideoDraw.BlendMask(frame, new AlphaMask(0, 0), 0, 0, new RgbaColor(1, 1, 1));
        }

        [Fact]
        public void FillBox_Should_Draw_Only_Inside_Its_Time_Window()
        {
            var transform = new FillBoxTransform(new VideoRectangle(0, 0, 2, 2), new RgbaColor(255, 255, 255), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));

            using var before = RunOne(transform, Solid(2, 2, Black));
            var inside = Solid(2, 2, Black);
            inside.Timestamp = TimeSpan.FromSeconds(1);
            using var during = RunOne(transform, inside);
            var atEnd = Solid(2, 2, Black);
            atEnd.Timestamp = TimeSpan.FromSeconds(2);
            using var after = RunOne(transform, atEnd);

            before.GetRow(VideoPlane.Y, 0)[0].Should().Be(16);
            during.GetRow(VideoPlane.Y, 0)[0].Should().Be(235);
            after.GetRow(VideoPlane.Y, 0)[0].Should().Be(16);
            FluentActions.Invoking(() => new FillBoxTransform(default, default, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1))).Should().Throw<ArgumentException>();
            transform.GetOutputSize(5, 6).Should().Be((5, 6));
            transform.CanChangeFrameCount.Should().BeFalse();
        }

        [Fact]
        public void Overlay_Should_Blend_The_Image_With_Alpha_And_Opacity_At_Odd_Negative_Positions()
        {
            var image = new RgbaImage(3, 3);
            for (var y = 0; y < 3; y++)
            {
                for (var x = 0; x < 3; x++)
                {
                    image.SetPixel(x, y, new RgbaColor(255, 255, 255, (byte)(x == 2 ? 0 : 255)));
                }
            }

            using var output = RunOne(new OverlayTransform(image, -1, 1, opacity: 1), Solid(4, 4, Black));
            using var half = RunOne(new OverlayTransform(image, 0, 0, opacity: 0.5), Solid(4, 4, Black));

            output.GetRow(VideoPlane.Y, 0).ToArray().Should().Equal(16, 16, 16, 16);
            output.GetRow(VideoPlane.Y, 1).ToArray().Should().Equal(235, 16, 16, 16);
            output.GetRow(VideoPlane.Y, 3).ToArray().Should().Equal(235, 16, 16, 16);
            half.GetRow(VideoPlane.Y, 0)[0].Should().Be(126);
        }

        [Fact]
        public void Overlay_Should_Convert_With_Each_Frames_Colour_Space_And_Respect_Its_Window()
        {
            var image = new RgbaImage(1, 1);
            image.SetPixel(0, 0, new RgbaColor(255, 255, 255));
            var transform = new OverlayTransform(image, 0, 0, end: TimeSpan.FromSeconds(1));

            using var limited = RunOne(transform, Solid(2, 2, Black));
            var fullFrame = VideoFrame.Create(2, 2, VideoColorRange.Full);
            fullFrame.Fill(new YuvColor(0, 128, 128));
            using var full = RunOne(transform, fullFrame);
            var late = Solid(2, 2, Black);
            late.Timestamp = TimeSpan.FromSeconds(1);
            using var after = RunOne(transform, late);

            limited.GetRow(VideoPlane.Y, 0)[0].Should().Be(235);
            full.GetRow(VideoPlane.Y, 0)[0].Should().Be(255);
            after.GetRow(VideoPlane.Y, 0)[0].Should().Be(16);
            RunOne(new OverlayTransform(new RgbaImage(0, 0), 0, 0), Solid(2, 2, Black)).Dispose();
        }

        [Fact]
        public void Overlay_Invalid_Arguments_Should_Throw()
        {
            FluentActions.Invoking(() => new OverlayTransform(null!, 0, 0)).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => new OverlayTransform(new RgbaImage(1, 1), 0, 0, 1.5)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => new OverlayTransform(new RgbaImage(1, 1), 0, 0, start: TimeSpan.FromSeconds(1), end: TimeSpan.Zero)).Should().Throw<ArgumentException>();
            new OverlayTransform(new RgbaImage(1, 1), 0, 0).GetOutputSize(3, 3).Should().Be((3, 3));
        }

        [Theory]
        [InlineData(VideoFadeDirection.In, 0, 16)]
        [InlineData(VideoFadeDirection.In, 500, 118)]
        [InlineData(VideoFadeDirection.In, 1000, 220)]
        [InlineData(VideoFadeDirection.Out, 0, 220)]
        [InlineData(VideoFadeDirection.Out, 500, 118)]
        [InlineData(VideoFadeDirection.Out, 2000, 16)]
        public void Fade_Should_Blend_With_The_Colour_By_Timestamp(VideoFadeDirection direction, int milliseconds, byte luma)
        {
            var transform = new VideoFadeTransform(direction, TimeSpan.Zero, TimeSpan.FromSeconds(1), Black);
            var frame = Solid(2, 2, new YuvColor(220, 128, 128));
            frame.Timestamp = TimeSpan.FromMilliseconds(milliseconds);

            using var output = RunOne(transform, frame);

            output.GetRow(VideoPlane.Y, 1)[1].Should().Be(luma);
        }

        [Fact]
        public void Fade_Invalid_Arguments_Should_Throw()
        {
            FluentActions.Invoking(() => new VideoFadeTransform((VideoFadeDirection)4, TimeSpan.Zero, TimeSpan.FromSeconds(1), Black)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => new VideoFadeTransform(VideoFadeDirection.In, TimeSpan.Zero, TimeSpan.Zero, Black)).Should().Throw<ArgumentOutOfRangeException>();
            var transform = new VideoFadeTransform(VideoFadeDirection.In, TimeSpan.Zero, TimeSpan.FromSeconds(2), Black);
            transform.ColorAmountAt(TimeSpan.FromSeconds(1)).Should().Be(0.5);
            transform.GetOutputSize(1, 2).Should().Be((1, 2));
            transform.CanChangeFrameCount.Should().BeFalse();
        }

        [Fact]
        public void Crossfade_Should_Mix_By_Progress_Into_Any_Destination()
        {
            using var from = Solid(3, 3, new YuvColor(16, 100, 100));
            using var to = Solid(3, 3, new YuvColor(216, 200, 140));
            using var destination = VideoFrame.Create(3, 3);

            VideoBlend.Crossfade(from, to, 0.25, destination);
            VideoBlend.Crossfade(from, to, 1, from);

            destination.GetRow(VideoPlane.Y, 2)[2].Should().Be(66);
            destination.GetRow(VideoPlane.U, 1)[1].Should().Be(125);
            from.GetRow(VideoPlane.V, 0)[0].Should().Be(140);
        }

        [Fact]
        public void Crossfade_Invalid_Arguments_Should_Throw()
        {
            using var a = VideoFrame.Create(2, 2);
            using var b = VideoFrame.Create(2, 4);
            using var full = VideoFrame.Create(2, 2, VideoColorRange.Full);

            FluentActions.Invoking(() => VideoBlend.Crossfade(a, b, 0.5, a)).Should().Throw<ArgumentException>();
            FluentActions.Invoking(() => VideoBlend.Crossfade(a, full, 0.5, a)).Should().Throw<ArgumentException>();
            FluentActions.Invoking(() => VideoBlend.Crossfade(a, a, 1.5, a)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => VideoBlend.Crossfade(null!, a, 0.5, a)).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => VideoBlend.Crossfade(a, null!, 0.5, a)).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => VideoBlend.Crossfade(a, a, 0.5, null!)).Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Overlay_Frame_Should_Copy_Opaque_And_Clip()
        {
            using var destination = Solid(6, 6, Black);
            using var source = Pattern(4, 4, (plane, x, y) => (byte)(plane == VideoPlane.Y ? 100 + (10 * y) + x : 200 + x + y));

            VideoBlend.Overlay(destination, source, -1, 3);
            VideoBlend.Overlay(destination, source, 50, 50);

            destination.GetRow(VideoPlane.Y, 3).ToArray().Should().Equal(101, 102, 103, 16, 16, 16);
            destination.GetRow(VideoPlane.Y, 5).ToArray().Should().Equal(121, 122, 123, 16, 16, 16);
            destination.GetRow(VideoPlane.Y, 2)[0].Should().Be(16);
            destination.GetRow(VideoPlane.U, 1).ToArray().Should().Equal(201, 128, 128);
            destination.GetRow(VideoPlane.U, 2).ToArray().Should().Equal(202, 128, 128);
            FluentActions.Invoking(() => VideoBlend.Overlay(destination, VideoFrame.Create(2, 2, VideoColorRange.Full), 0, 0)).Should().Throw<ArgumentException>();
            FluentActions.Invoking(() => VideoBlend.Overlay(null!, source, 0, 0)).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => VideoBlend.Overlay(destination, null!, 0, 0)).Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void VideoRectangle_Should_Clip_And_Fit()
        {
            new VideoRectangle(-2, 3, 10, 10).ClipTo(5, 8).Should().Be(new VideoRectangle(0, 3, 5, 5));
            new VideoRectangle(6, 0, 2, 2).ClipTo(5, 5).Should().Be(default(VideoRectangle));
            new VideoRectangle(1, 1, 3, 3).FitsIn(4, 4).Should().BeTrue();
            new VideoRectangle(1, 1, 4, 3).FitsIn(4, 4).Should().BeFalse();
            new VideoRectangle(1, 2, 3, 4).Right.Should().Be(4);
            new VideoRectangle(1, 2, 3, 4).Bottom.Should().Be(6);
        }
    }
}
