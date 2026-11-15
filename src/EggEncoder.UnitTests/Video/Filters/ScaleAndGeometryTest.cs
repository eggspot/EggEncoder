using EggEncoder.Video;
using EggEncoder.Video.Filters;
using FluentAssertions;
using static EggEncoder.UnitTests.Video.Filters.FrameTestSupport;

namespace EggEncoder.UnitTests.Video.Filters
{
    public class ScaleTransformTest
    {
        public static TheoryData<string, ScaleKernel, int, int, double, double> FfmpegScales => new()
        {
            { "ffmpeg_bicubic_40x30.yuv", ScaleKernel.Bicubic, 40, 30, 35, 35 },
            { "ffmpeg_bicubic_100x76.yuv", ScaleKernel.Bicubic, 100, 76, 35, 35 },
            { "ffmpeg_lanczos_40x30.yuv", ScaleKernel.Lanczos, 40, 30, 35, 35 },
            { "ffmpeg_lanczos_100x76.yuv", ScaleKernel.Lanczos, 100, 76, 35, 35 }
        };

        [Theory]
        [MemberData(nameof(FfmpegScales))]
        public void Scale_Should_Stay_Close_To_Ffmpeg(string fixture, ScaleKernel kernel, int width, int height, double lumaFloor, double chromaFloor)
        {
            using var expected = Load(fixture, width, height);
            using var actual = RunOne(new ScaleTransform(width, height, kernel), Input());

            Psnr(actual, expected, VideoPlane.Y).Should().BeGreaterThan(lumaFloor);
            Psnr(actual, expected, VideoPlane.U).Should().BeGreaterThan(chromaFloor);
            Psnr(actual, expected, VideoPlane.V).Should().BeGreaterThan(chromaFloor);
        }

        [Theory]
        [InlineData(ScaleKernel.Bilinear)]
        [InlineData(ScaleKernel.Bicubic)]
        [InlineData(ScaleKernel.Lanczos)]
        public void Scale_To_The_Same_Size_Should_Be_Exact(ScaleKernel kernel)
        {
            using var input = Input();
            using var copy = input.Clone();

            using var output = RunOne(new ScaleTransform(64, 48, kernel), copy);

            SamePixels(output, input).Should().BeTrue();
        }

        [Theory]
        [InlineData(ScaleKernel.Bilinear, 17, 9)]
        [InlineData(ScaleKernel.Bicubic, 300, 200)]
        [InlineData(ScaleKernel.Lanczos, 5, 3)]
        public void Scale_Of_A_Flat_Frame_Should_Stay_Flat(ScaleKernel kernel, int width, int height)
        {
            using var output = RunOne(new ScaleTransform(width, height, kernel), Solid(64, 48, new YuvColor(77, 140, 90)));

            output.GetRow(VideoPlane.Y, height - 1).ToArray().Should().OnlyContain(b => b == 77);
            output.GetRow(VideoPlane.U, 0).ToArray().Should().OnlyContain(b => b == 140);
            output.GetRow(VideoPlane.V, output.ChromaHeight - 1).ToArray().Should().OnlyContain(b => b == 90);
        }

        [Theory]
        [InlineData(ScaleKernel.Bilinear, 64, 7, 1.0)]
        [InlineData(ScaleKernel.Bicubic, 64, 300, 0.2)]
        [InlineData(ScaleKernel.Lanczos, 3, 64, 21.0)]
        public void Weights_Should_Sum_To_One_In_Fixed_Point(ScaleKernel kernel, int sourceLength, int outputs, double scale)
        {
            var weights = PlaneScaler.AxisWeights.Build(kernel, outputs, scale, n => ((n + 0.5) * scale) - 0.5, 0, sourceLength - 1);

            for (var n = 0; n < outputs; n++)
            {
                var sum = 0;
                for (var tap = 0; tap < weights.Taps; tap++)
                {
                    sum += weights.Weights[(n * weights.Taps) + tap];
                }

                sum.Should().Be(16384);
                weights.Starts[n].Should().BeInRange(0, sourceLength - weights.Taps);
            }
        }

        [Fact]
        public void Scale_With_Many_Threads_Should_Equal_One_Thread()
        {
            using var serial = RunOne(new ScaleTransform(301, 199, ScaleKernel.Lanczos), Input());
            using var parallel = RunOne(new ScaleTransform(301, 199, ScaleKernel.Lanczos, maxDegreeOfParallelism: 0), Input());

            SamePixels(parallel, serial).Should().BeTrue();
        }

        [Fact]
        public void Scale_Should_Keep_Timing_And_Colors_And_Reuse_Weights_Across_Frames()
        {
            var transform = new ScaleTransform(32, 24);
            using var first = RunOne(transform, Input());
            var input = Input();
            input.Timestamp = TimeSpan.FromSeconds(3);
            input.Duration = TimeSpan.FromMilliseconds(40);
            input.Range = VideoColorRange.Full;

            using var second = RunOne(transform, input);

            second.Timestamp.Should().Be(TimeSpan.FromSeconds(3));
            second.Duration.Should().Be(TimeSpan.FromMilliseconds(40));
            second.Range.Should().Be(VideoColorRange.Full);
            SamePixels(second, first).Should().BeTrue();
            transform.GetOutputSize(1, 1).Should().Be((32, 24));
            transform.CanChangeFrameCount.Should().BeFalse();
        }

        [Fact]
        public void Scale_From_A_Source_Rectangle_Should_Equal_Crop_Then_Scale()
        {
            using var direct = RunOne(new ScaleTransform(20, 30, ScaleKernel.Bicubic, new VideoRectangle(10, 6, 40, 36)), Input());
            using var cropped = RunOne(new CropTransform(new VideoRectangle(10, 6, 40, 36)), Input());
            using var viaCrop = RunOne(new ScaleTransform(20, 30, ScaleKernel.Bicubic), cropped.Clone());

            SamePixels(direct, viaCrop).Should().BeTrue();
        }

        [Fact]
        public void Source_Outside_The_Frame_Should_Throw_And_Dispose_The_Frame()
        {
            var input = Input();

            FluentActions.Invoking(() => RunOne(new ScaleTransform(10, 10, source: new VideoRectangle(60, 0, 10, 10)), input))
                .Should().Throw<ArgumentException>();
            input.Invoking(f => f.PlaneY.Length).Should().Throw<ObjectDisposedException>();
        }

        [Fact]
        public void Constructor_Should_Validate()
        {
            FluentActions.Invoking(() => new ScaleTransform(0, 1)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => new ScaleTransform(1, 0)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => new ScaleTransform(16385, 1)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => new ScaleTransform(1, 16385)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => new ScaleTransform(1, 1, (ScaleKernel)9)).Should().Throw<ArgumentOutOfRangeException>();
        }
    }

    public class CropPadFitRotateTest
    {
        [Fact]
        public void Crop_Should_Copy_The_Rectangle_And_Round_Odd_Edges_Down()
        {
            using var input = Pattern(9, 7, (plane, x, y) => (byte)((plane == VideoPlane.Y ? 0 : 100) + (10 * y) + x));

            using var output = RunOne(new CropTransform(new VideoRectangle(3, 1, 5, 4)), input.Clone());

            output.Width.Should().Be(5);
            output.Height.Should().Be(4);
            output.GetRow(VideoPlane.Y, 0).ToArray().Should().Equal(2, 3, 4, 5, 6);
            output.GetRow(VideoPlane.Y, 3).ToArray().Should().Equal(32, 33, 34, 35, 36);
            output.GetRow(VideoPlane.U, 0).ToArray().Should().Equal(101, 102, 103);
            output.GetRow(VideoPlane.V, 1).ToArray().Should().Equal(111, 112, 113);
        }

        [Fact]
        public void Crop_Outside_The_Frame_Should_Throw()
        {
            FluentActions.Invoking(() => RunOne(new CropTransform(new VideoRectangle(0, 0, 65, 10)), Input())).Should().Throw<ArgumentException>();
            FluentActions.Invoking(() => new CropTransform(new VideoRectangle(-2, 0, 5, 5))).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => new CropTransform(new VideoRectangle(0, 0, 0, 5))).Should().Throw<ArgumentOutOfRangeException>();
            new CropTransform(new VideoRectangle(1, 1, 5, 6)).GetOutputSize(100, 100).Should().Be((5, 6));
        }

        [Fact]
        public void Pad_Should_Fill_The_Canvas_And_Place_The_Frame_On_Even_Coordinates()
        {
            using var output = RunOne(new PadTransform(8, 6, 3, 2, new YuvColor(16, 128, 128)), Solid(3, 3, new YuvColor(200, 50, 60)));

            output.GetRow(VideoPlane.Y, 2).ToArray().Should().Equal(16, 16, 200, 200, 200, 16, 16, 16);
            output.GetRow(VideoPlane.Y, 1).ToArray().Should().OnlyContain(b => b == 16);
            output.GetRow(VideoPlane.U, 1).ToArray().Should().Equal(128, 50, 50, 128);
            output.GetRow(VideoPlane.V, 2).ToArray().Should().Equal(128, 60, 60, 128);
        }

        [Fact]
        public void Pad_Too_Small_Or_Invalid_Should_Throw()
        {
            FluentActions.Invoking(() => RunOne(new PadTransform(10, 10, 4, 0, default), Solid(8, 8, default))).Should().Throw<ArgumentException>();
            FluentActions.Invoking(() => new PadTransform(0, 10, 0, 0, default)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => new PadTransform(10, 10, -1, 0, default)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => new PadTransform(10, 16385, 0, 0, default)).Should().Throw<ArgumentOutOfRangeException>();
            new PadTransform(10, 12, 0, 0, default).GetOutputSize(1, 1).Should().Be((10, 12));
        }

        [Theory]
        [InlineData(FitMode.Contain, 1920, 1080, 1080, 1920, 1080, 608, null)]
        [InlineData(FitMode.Contain, 1080, 1920, 1080, 1920, 1080, 1920, null)]
        [InlineData(FitMode.Contain, 640, 480, 721, 405, 540, 405, null)]
        [InlineData(FitMode.Cover, 1920, 1080, 1080, 1920, 1080, 1920, "656,0,608,1080")]
        [InlineData(FitMode.Cover, 1080, 1920, 1920, 1080, 1920, 1080, "0,656,1080,608")]
        [InlineData(FitMode.Cover, 64, 48, 30, 40, 30, 40, "14,0,36,48")]
        [InlineData(FitMode.Cover, 63, 47, 31, 41, 31, 41, "13,0,36,47")]
        public void Fit_Should_Plan_Even_Sizes_Centred(FitMode mode, int width, int height, int canvasWidth, int canvasHeight, int scaledWidth, int scaledHeight, string? source)
        {
            var plan = new FitTransform(canvasWidth, canvasHeight, mode).Plan(width, height);

            plan.Width.Should().Be(scaledWidth);
            plan.Height.Should().Be(scaledHeight);
            (plan.Source is { } s ? $"{s.X},{s.Y},{s.Width},{s.Height}" : null).Should().Be(source);
        }

        [Fact]
        public void Fit_Cover_Should_Match_Ffmpeg_Crop_Then_Scale()
        {
            using var expected = Load("ffmpeg_cover_30x40.yuv", 30, 40);

            using var actual = RunOne(new FitTransform(30, 40, FitMode.Cover), Input());

            Psnr(actual, expected, VideoPlane.Y).Should().BeGreaterThan(35);
            Psnr(actual, expected, VideoPlane.U).Should().BeGreaterThan(35);
        }

        [Fact]
        public void Fit_Contain_Should_Letterbox_With_The_Pad_Colour()
        {
            var transform = new FitTransform(64, 64, FitMode.Contain, new YuvColor(16, 128, 128));

            using var output = RunOne(transform, Solid(64, 32, new YuvColor(200, 100, 150)));
            using var second = RunOne(transform, Solid(64, 32, new YuvColor(200, 100, 150)));

            output.GetRow(VideoPlane.Y, 0).ToArray().Should().OnlyContain(b => b == 16);
            output.GetRow(VideoPlane.Y, 16).ToArray().Should().OnlyContain(b => b == 200);
            output.GetRow(VideoPlane.Y, 47).ToArray().Should().OnlyContain(b => b == 200);
            output.GetRow(VideoPlane.Y, 48).ToArray().Should().OnlyContain(b => b == 16);
            SamePixels(second, output).Should().BeTrue();
            transform.GetOutputSize(1, 1).Should().Be((64, 64));
        }

        [Fact]
        public void Fit_Invalid_Arguments_Should_Throw()
        {
            FluentActions.Invoking(() => new FitTransform(0, 10, FitMode.Cover)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => new FitTransform(10, 16385, FitMode.Cover)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => new FitTransform(10, 10, (FitMode)5)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => new FitTransform(10, 10, FitMode.Cover, kernel: (ScaleKernel)5)).Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Rotate_90_Should_Match_Ffmpeg_Transpose_Clock_Exactly()
        {
            using var expected = Load("ffmpeg_transpose_clock.yuv", 48, 64);

            using var actual = RunOne(new RotateTransform(90), Input());

            SamePixels(actual, expected).Should().BeTrue();
        }

        [Theory]
        [InlineData(90)]
        [InlineData(180)]
        [InlineData(270)]
        public void Rotate_Should_Map_Pixels_Clockwise_On_Odd_Sizes(int degrees)
        {
            using var input = Pattern(5, 3, (plane, x, y) => (byte)((plane == VideoPlane.Y ? 0 : 100) + (10 * y) + x));
            var transform = new RotateTransform(degrees);

            using var output = RunOne(transform, input.Clone());

            output.Width.Should().Be(degrees == 180 ? 5 : 3);
            transform.GetOutputSize(5, 3).Should().Be((output.Width, output.Height));
            var corner = output.GetRow(VideoPlane.Y, 0)[0];
            corner.Should().Be(degrees switch { 90 => 20, 180 => 24, _ => 4 });
            using var back = RunOne(new RotateTransform(degrees == 180 ? 180 : 360 - degrees), output.Clone());
            SamePixels(back, input).Should().BeTrue();
        }

        [Fact]
        public void Rotate_Invalid_Angle_Should_Throw()
        {
            FluentActions.Invoking(() => new RotateTransform(45)).Should().Throw<ArgumentOutOfRangeException>();
            new RotateTransform(90).CanChangeFrameCount.Should().BeFalse();
        }
    }
}
