using EggEncoder.Video;
using FluentAssertions;

namespace EggEncoder.UnitTests.Video
{
    public class VideoFrameTest
    {
        [Theory]
        [InlineData(1, 1, 1, 1)]
        [InlineData(7, 5, 4, 3)]
        [InlineData(1920, 1080, 960, 540)]
        [InlineData(1081, 1919, 541, 960)]
        public void Create_Should_Size_The_Chroma_Planes_By_Rounding_Up(int width, int height, int chromaWidth, int chromaHeight)
        {
            using var frame = new VideoFramePool().Rent(width, height);

            frame.ChromaWidth.Should().Be(chromaWidth);
            frame.ChromaHeight.Should().Be(chromaHeight);
            frame.StrideY.Should().BeGreaterThanOrEqualTo(width);
            (frame.StrideY % 64).Should().Be(0);
            (frame.StrideUV % 64).Should().Be(0);
            frame.PlaneY.Length.Should().Be((frame.StrideY * (height - 1)) + width);
            frame.PlaneU.Length.Should().Be((frame.StrideUV * (chromaHeight - 1)) + chromaWidth);
            frame.PlaneV.Length.Should().Be(frame.PlaneU.Length);
            frame.GetRow(VideoPlane.Y, height - 1).Length.Should().Be(width);
            frame.GetRow(VideoPlane.V, chromaHeight - 1).Length.Should().Be(chromaWidth);
        }

        [Fact]
        public void Planes_Should_Not_Overlap()
        {
            using var frame = new VideoFramePool().Rent(7, 5);

            frame.PlaneY.Fill(1);
            frame.PlaneU.Fill(2);
            frame.PlaneV.Fill(3);

            frame.GetRow(VideoPlane.Y, 4).ToArray().Should().OnlyContain(b => b == 1);
            frame.GetRow(VideoPlane.U, 2).ToArray().Should().OnlyContain(b => b == 2);
            frame.GetRow(VideoPlane.V, 0).ToArray().Should().OnlyContain(b => b == 3);
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(1, 0)]
        [InlineData(16385, 1)]
        [InlineData(1, 16385)]
        public void Create_OutOfRangeSize_Should_Throw(int width, int height)
        {
            FluentActions.Invoking(() => VideoFrame.Create(width, height)).Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Create_UnknownRangeOrMatrix_Should_Throw()
        {
            FluentActions.Invoking(() => VideoFrame.Create(2, 2, (VideoColorRange)9)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => VideoFrame.Create(2, 2, matrix: (VideoColorMatrix)9)).Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Create_Should_Carry_The_Range_And_Matrix()
        {
            using var frame = VideoFrame.Create(4, 4, VideoColorRange.Full, VideoColorMatrix.Bt601);

            frame.Range.Should().Be(VideoColorRange.Full);
            frame.Matrix.Should().Be(VideoColorMatrix.Bt601);
        }

        [Fact]
        public void GetRow_OutsideThePlane_Should_Throw()
        {
            using var frame = VideoFrame.Create(4, 4);

            frame.Invoking(f => f.GetRow(VideoPlane.Y, 4)).Should().Throw<ArgumentOutOfRangeException>();
            frame.Invoking(f => f.GetRow(VideoPlane.U, 2)).Should().Throw<ArgumentOutOfRangeException>();
            frame.Invoking(f => f.GetRow(VideoPlane.U, -1)).Should().Throw<ArgumentOutOfRangeException>();
            frame.Invoking(f => f.GetRow((VideoPlane)7, 0)).Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void StrideOf_Should_Name_The_Plane_Stride()
        {
            using var frame = VideoFrame.Create(100, 10);

            frame.StrideOf(VideoPlane.Y).Should().Be(frame.StrideY);
            frame.StrideOf(VideoPlane.U).Should().Be(frame.StrideUV);
        }

        [Fact]
        public void Fill_Should_Set_Visible_Pixels_Only()
        {
            var pool = new VideoFramePool();
            using var frame = pool.Rent(7, 5);
            frame.Buffer.AsSpan().Fill(0xEE);

            frame.Fill(new YuvColor(16, 128, 200));

            for (var row = 0; row < 5; row++)
            {
                frame.GetRow(VideoPlane.Y, row).ToArray().Should().OnlyContain(b => b == 16);
                frame.Buffer[frame.OffsetY + (row * frame.StrideY) + 7].Should().Be(0xEE);
            }

            frame.GetRow(VideoPlane.U, 2).ToArray().Should().OnlyContain(b => b == 128);
            frame.GetRow(VideoPlane.V, 2).ToArray().Should().OnlyContain(b => b == 200);
            frame.Buffer[frame.OffsetV + (2 * frame.StrideUV) + 4].Should().Be(0xEE);
        }

        [Fact]
        public void Clone_Should_Copy_Pixels_And_Metadata_Into_A_New_Buffer()
        {
            var pool = new VideoFramePool();
            using var frame = pool.Rent(5, 3, VideoColorRange.Full, VideoColorMatrix.Bt601);
            frame.Fill(new YuvColor(1, 2, 3));
            frame.GetRow(VideoPlane.Y, 2)[4] = 99;
            frame.Timestamp = TimeSpan.FromSeconds(2);
            frame.Duration = TimeSpan.FromMilliseconds(40);

            using var clone = frame.Clone();
            frame.GetRow(VideoPlane.Y, 0)[0] = 50;

            clone.GetRow(VideoPlane.Y, 2)[4].Should().Be(99);
            clone.GetRow(VideoPlane.Y, 0)[0].Should().Be(1);
            clone.GetRow(VideoPlane.V, 1)[2].Should().Be(3);
            clone.Range.Should().Be(VideoColorRange.Full);
            clone.Matrix.Should().Be(VideoColorMatrix.Bt601);
            clone.Timestamp.Should().Be(TimeSpan.FromSeconds(2));
            clone.Duration.Should().Be(TimeSpan.FromMilliseconds(40));
            clone.Buffer.Should().NotBeSameAs(frame.Buffer);
        }

        [Fact]
        public void CopyTo_DifferentSize_Or_Null_Should_Throw()
        {
            using var frame = VideoFrame.Create(4, 4);
            using var other = VideoFrame.Create(4, 6);

            frame.Invoking(f => f.CopyTo(other)).Should().Throw<ArgumentException>();
            frame.Invoking(f => f.CopyTo(null!)).Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Dispose_Should_Be_Idempotent_And_Block_Plane_Access()
        {
            var pool = new VideoFramePool();
            var frame = pool.Rent(4, 4);

            frame.Dispose();
            frame.Dispose();

            pool.RetainedBufferCount.Should().Be(1);
            frame.Invoking(f => f.PlaneY.Length).Should().Throw<ObjectDisposedException>();
            frame.Invoking(f => f.Fill(default)).Should().Throw<ObjectDisposedException>();
            frame.Invoking(f => f.Clone()).Should().Throw<ObjectDisposedException>();
            frame.Width.Should().Be(4);
        }

        [Fact]
        public void Padded_Frame_Should_Start_Its_Planes_Inside_The_Border()
        {
            var pool = new VideoFramePool();
            using var frame = pool.Rent(16, 16, VideoColorRange.Limited, VideoColorMatrix.Bt709, padding: 32);
            frame.Buffer.AsSpan().Clear();

            frame.Fill(new YuvColor(200, 100, 50));

            frame.OffsetY.Should().Be((32 * frame.StrideY) + 32);
            frame.StrideY.Should().Be(128);
            frame.OffsetU.Should().Be((frame.StrideY * 80) + (16 * frame.StrideUV) + 16);
            frame.Buffer[frame.OffsetY - 1].Should().Be(0);
            frame.Buffer[frame.OffsetY - frame.StrideY].Should().Be(0);
            frame.Buffer[frame.OffsetU - 1].Should().Be(0);
            frame.PlaneY[0].Should().Be(200);
            frame.PlaneU[0].Should().Be(100);
            frame.PlaneV[(7 * frame.StrideUV) + 7].Should().Be(50);
        }

        [Fact]
        public void Padded_Clone_Should_Keep_The_Padding()
        {
            var pool = new VideoFramePool();
            using var frame = pool.Rent(8, 8, VideoColorRange.Limited, VideoColorMatrix.Bt709, padding: 32);

            using var clone = frame.Clone();

            clone.Padding.Should().Be(32);
            clone.OffsetY.Should().Be(frame.OffsetY);
        }
    }

    public class VideoFramePoolTest
    {
        [Fact]
        public void Rent_After_Dispose_Should_Reuse_The_Buffer_Of_The_Same_Size_Only()
        {
            var pool = new VideoFramePool();
            var first = pool.Rent(64, 32);
            var buffer = first.Buffer;
            first.Dispose();

            using var other = pool.Rent(32, 64);
            using var same = pool.Rent(64, 32);

            other.Buffer.Should().NotBeSameAs(buffer);
            same.Buffer.Should().BeSameAs(buffer);
            pool.RetainedBufferCount.Should().Be(0);
        }

        [Fact]
        public void Pool_Should_Keep_At_Most_MaxRetainedPerSize_Buffers_Per_Size()
        {
            var pool = new VideoFramePool(maxRetainedPerSize: 8);
            var frames = Enumerable.Range(0, 10).Select(_ => pool.Rent(16, 16)).ToList();
            var bytesEach = frames[0].Buffer.Length;

            frames.ForEach(f => f.Dispose());

            pool.MaxRetainedPerSize.Should().Be(8);
            pool.RetainedBufferCount.Should().Be(8);
            pool.RetainedBytes.Should().Be(8L * bytesEach);
        }

        [Fact]
        public void Clear_Should_Drop_Every_Free_Buffer()
        {
            var pool = new VideoFramePool();
            pool.Rent(16, 16).Dispose();

            pool.Clear();

            pool.RetainedBufferCount.Should().Be(0);
            pool.RetainedBytes.Should().Be(0);
        }

        [Fact]
        public void Zero_Retained_Should_Keep_Nothing_And_Negative_Should_Throw()
        {
            var pool = new VideoFramePool(0);
            pool.Rent(8, 8).Dispose();

            pool.RetainedBufferCount.Should().Be(0);
            FluentActions.Invoking(() => new VideoFramePool(-1)).Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Frames_Disposed_On_Many_Threads_Should_Keep_The_Count_Consistent()
        {
            var pool = new VideoFramePool(maxRetainedPerSize: 1000);
            var frames = Enumerable.Range(0, 400).Select(_ => pool.Rent(8, 8)).ToArray();

            Parallel.ForEach(frames, f => f.Dispose());

            pool.RetainedBufferCount.Should().Be(400);
        }

        [Fact]
        public void Shared_Should_Be_Used_By_Create()
        {
            VideoFramePool.Shared.Should().NotBeNull();
            using var frame = VideoFrame.Create(3, 3);

            frame.Pool.Should().BeSameAs(VideoFramePool.Shared);
        }
    }

    public class VideoColorTest
    {
        [Theory]
        [InlineData(255, 255, 255, VideoColorMatrix.Bt709, VideoColorRange.Limited, 235, 128, 128)]
        [InlineData(0, 0, 0, VideoColorMatrix.Bt709, VideoColorRange.Limited, 16, 128, 128)]
        [InlineData(255, 0, 0, VideoColorMatrix.Bt709, VideoColorRange.Limited, 63, 102, 240)]
        [InlineData(255, 0, 0, VideoColorMatrix.Bt601, VideoColorRange.Full, 76, 85, 255)]
        [InlineData(255, 0, 0, VideoColorMatrix.Bt601, VideoColorRange.Limited, 81, 90, 240)]
        [InlineData(0, 0, 255, VideoColorMatrix.Bt709, VideoColorRange.Full, 18, 255, 116)]
        public void FromRgb_Should_Match_The_Standards(byte r, byte g, byte b, VideoColorMatrix matrix, VideoColorRange range, byte y, byte u, byte v)
        {
            YuvColor.FromRgb(r, g, b, matrix, range).Should().Be(new YuvColor(y, u, v));
            new RgbaColor(r, g, b).ToYuv(matrix, range).Should().Be(new YuvColor(y, u, v));
        }

        [Theory]
        [InlineData(VideoColorMatrix.Bt601, VideoColorRange.Limited)]
        [InlineData(VideoColorMatrix.Bt601, VideoColorRange.Full)]
        [InlineData(VideoColorMatrix.Bt709, VideoColorRange.Limited)]
        [InlineData(VideoColorMatrix.Bt709, VideoColorRange.Full)]
        public void RgbRoundTrip_Should_Stay_Within_Two_Levels(VideoColorMatrix matrix, VideoColorRange range)
        {
            for (var r = 0; r < 256; r += 17)
            {
                for (var g = 0; g < 256; g += 17)
                {
                    for (var b = 0; b < 256; b += 17)
                    {
                        var back = YuvColor.FromRgb((byte)r, (byte)g, (byte)b, matrix, range).ToRgb(matrix, range);
                        Math.Abs(back.R - r).Should().BeLessThanOrEqualTo(2);
                        Math.Abs(back.G - g).Should().BeLessThanOrEqualTo(2);
                        Math.Abs(back.B - b).Should().BeLessThanOrEqualTo(2);
                    }
                }
            }
        }

        [Fact]
        public void UnknownMatrix_Should_Throw()
        {
            FluentActions.Invoking(() => YuvColor.FromRgb(1, 2, 3, (VideoColorMatrix)5, VideoColorRange.Full)).Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void RgbaColor_Should_Default_To_Opaque()
        {
            new RgbaColor(1, 2, 3).A.Should().Be(255);
        }
    }

    public class VideoFrameRateTest
    {
        [Fact]
        public void TimestampOf_Should_Be_Exact_From_The_Index()
        {
            var ntsc = new VideoFrameRate(30000, 1001);

            ntsc.TimestampOf(1_000_000).Should().Be(TimeSpan.FromTicks(333_666_666_667));
            ntsc.TimestampOf(30000).Should().Be(TimeSpan.FromSeconds(1001));
            ntsc.FrameDuration.Should().Be(TimeSpan.FromTicks(333_667));
            ntsc.FramesPerSecond.Should().BeApproximately(29.97, 0.001);
            ntsc.ToString().Should().Be("30000/1001");
        }

        [Fact]
        public void NearestFrameIndex_Should_Round_Halves_Away_From_Zero()
        {
            var rate = new VideoFrameRate(25);

            rate.NearestFrameIndex(TimeSpan.FromMilliseconds(20)).Should().Be(1);
            rate.NearestFrameIndex(TimeSpan.FromMilliseconds(19.9)).Should().Be(0);
            rate.NearestFrameIndex(TimeSpan.FromMilliseconds(-20)).Should().Be(-1);
            rate.NearestFrameIndex(TimeSpan.FromSeconds(2)).Should().Be(50);
            rate.TimestampOf(-3).Should().Be(TimeSpan.FromMilliseconds(-120));
        }

        [Fact]
        public void NonPositive_Parts_Should_Throw()
        {
            FluentActions.Invoking(() => new VideoFrameRate(0)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => new VideoFrameRate(30, 0)).Should().Throw<ArgumentOutOfRangeException>();
        }
    }

    public class RgbaImageAndAlphaMaskTest
    {
        [Fact]
        public void RgbaImage_Should_Get_And_Set_Pixels()
        {
            var image = new RgbaImage(3, 2);

            image.SetPixel(2, 1, new RgbaColor(1, 2, 3, 4));

            image.GetPixel(2, 1).Should().Be(new RgbaColor(1, 2, 3, 4));
            image.Pixels[20..24].ToArray().Should().Equal(1, 2, 3, 4);
            image.GetPixel(0, 0).Should().Be(new RgbaColor(0, 0, 0, 0));
        }

        [Fact]
        public void RgbaImage_Invalid_Arguments_Should_Throw()
        {
            var image = new RgbaImage(3, 2);

            image.Invoking(i => i.GetPixel(3, 0)).Should().Throw<ArgumentOutOfRangeException>();
            image.Invoking(i => i.SetPixel(0, 2, default)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => new RgbaImage(2, 2, new byte[15])).Should().Throw<ArgumentException>();
            FluentActions.Invoking(() => new RgbaImage(2, 2, null!)).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => new RgbaImage(-1, 2)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => new RgbaImage(100000, 100000)).Should().Throw<ArgumentOutOfRangeException>();
            new RgbaImage(0, 0).Pixels.Length.Should().Be(0);
        }

        [Fact]
        public void AlphaMask_Should_Wrap_Its_Coverage()
        {
            var coverage = new byte[6];
            var mask = new AlphaMask(3, 2, coverage);

            mask.Coverage[5] = 255;

            coverage[5].Should().Be(255);
            mask.Width.Should().Be(3);
            mask.Height.Should().Be(2);
            new AlphaMask(4, 4).Coverage.Length.Should().Be(16);
        }

        [Fact]
        public void AlphaMask_Invalid_Arguments_Should_Throw()
        {
            FluentActions.Invoking(() => new AlphaMask(2, 2, new byte[3])).Should().Throw<ArgumentException>();
            FluentActions.Invoking(() => new AlphaMask(2, 2, null!)).Should().Throw<ArgumentNullException>();
            FluentActions.Invoking(() => new AlphaMask(2, -2)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => new AlphaMask(int.MaxValue, 2)).Should().Throw<ArgumentOutOfRangeException>();
        }
    }
}
