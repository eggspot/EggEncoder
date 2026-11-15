using EggEncoder.Codecs.H264;
using EggEncoder.Containers.Mp4;
using EggEncoder.Media;
using EggEncoder.UnitTests.Codecs.Jpeg;
using EggEncoder.UnitTests.TestUtilities;
using EggEncoder.Video;
using EggEncoder.Video.Filters;
using FluentAssertions;

namespace EggEncoder.UnitTests.Containers.Mp4
{
    public class Mp4VideoReaderTest
    {
        private static readonly TimeSpan FrameStep = TimeSpan.FromSeconds(0.04);

        [Fact]
        public void Properties_Should_Describe_The_Rotated_H264_Track()
        {
            using var reader = Mp4VideoReader.Open(Mp4FileSourceTest.H264AacFixture);

            reader.CodedSize.Should().Be((64, 48));
            reader.DisplaySize.Should().Be((48, 64));
            reader.Rotation.Should().Be(270);
            reader.Duration.Should().Be(TimeSpan.FromSeconds(0.4));
            reader.HasAudio.Should().BeTrue();
        }

        [Fact]
        public void ReadFrames_Should_Return_Every_Frame_Upright_In_Display_Order()
        {
            using var reader = Mp4VideoReader.Open(Mp4FileSourceTest.H264AacFixture);

            var frames = reader.ReadFrames().ToList();

            try
            {
                frames.Select(f => f.Timestamp).Should().Equal(Enumerable.Range(0, 10).Select(i => FrameStep * i));
                frames.Should().OnlyContain(f => f.Width == 48 && f.Height == 64);
                var expected = DecodeDirectly();
                try
                {
                    for (var i = 0; i < frames.Count; i++)
                    {
                        JpegEncoderTest.Plane(frames[i], VideoPlane.Y).Should().Equal(JpegEncoderTest.Plane(expected[i], VideoPlane.Y));
                        JpegEncoderTest.Plane(frames[i], VideoPlane.V).Should().Equal(JpegEncoderTest.Plane(expected[i], VideoPlane.V));
                    }
                }
                finally
                {
                    expected.ForEach(f => f.Dispose());
                }
            }
            finally
            {
                frames.ForEach(f => f.Dispose());
            }
        }

        [Theory]
        [InlineData(0.2, 0.2, 5)]
        [InlineData(0.25, 0.28, 3)]
        [InlineData(0.0, 0.0, 10)]
        public void ReadFrames_From_A_Start_Should_Drop_Frames_Shown_Before_It(double start, double first, int count)
        {
            using var reader = Mp4VideoReader.Open(Mp4FileSourceTest.H264AacFixture);

            var frames = reader.ReadFrames(TimeSpan.FromSeconds(start)).ToList();

            frames.Should().HaveCount(count);
            frames[0].Timestamp.Should().Be(TimeSpan.FromSeconds(first));
            frames.ForEach(f => f.Dispose());
        }

        [Theory]
        [InlineData(0.1, 0.12)]
        [InlineData(0.0, 0.0)]
        [InlineData(0.36, 0.36)]
        [InlineData(10.0, 0.36)]
        public void GrabFrame_Should_Pick_The_First_Frame_At_Or_After_The_Position_Or_The_Last(double position, double expected)
        {
            using var reader = Mp4VideoReader.Open(Mp4FileSourceTest.H264AacFixture);

            using var frame = reader.GrabFrame(TimeSpan.FromSeconds(position));

            frame.Timestamp.Should().Be(TimeSpan.FromSeconds(expected));
            frame.Width.Should().Be(48);
        }

        [Fact]
        public void GrabFrame_Negative_Position_Should_Throw()
        {
            using var reader = Mp4VideoReader.Open(Mp4FileSourceTest.H264AacFixture);

            var act = () => reader.GrabFrame(TimeSpan.FromSeconds(-1));

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void GrabJpeg_Should_Encode_The_Grabbed_Frame()
        {
            using var reader = Mp4VideoReader.Open(Mp4FileSourceTest.H264AacFixture);
            using var frame = reader.GrabFrame(TimeSpan.FromSeconds(0.1));

            var decoded = BaselineJpegDecoder.Decode(reader.GrabJpeg(TimeSpan.FromSeconds(0.1), 2));

            decoded.Width.Should().Be(48);
            decoded.Height.Should().Be(64);
            using var full = ToFullRange(frame);
            JpegEncoderTest.Psnr(decoded.Planes[0], JpegEncoderTest.Plane(full, VideoPlane.Y)).Should().BeGreaterThan(35);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(32)]
        public void GrabJpeg_Scale_Out_Of_Range_Should_Throw(int scale)
        {
            using var reader = Mp4VideoReader.Open(Mp4FileSourceTest.H264AacFixture);

            var act = () => reader.GrabJpeg(TimeSpan.Zero, scale);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Constructor_Without_H264_Track_Should_Throw()
        {
            var act = () => Mp4VideoReader.Open(Mp4FileSourceTest.HevcFixture);

            act.Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void Methods_After_Dispose_Should_Throw()
        {
            var reader = Mp4VideoReader.Open(Mp4FileSourceTest.H264AacFixture);
            reader.Dispose();
            reader.Dispose();

            ((Action)(() => reader.ReadFrames())).Should().Throw<ObjectDisposedException>();
            ((Action)(() => reader.GrabFrame(TimeSpan.Zero))).Should().Throw<ObjectDisposedException>();
        }

        [Fact]
        public void ReadFrames_Abandoned_Early_Should_Dispose_Frames_It_Still_Holds()
        {
            var pool = new VideoFramePool();
            using var reader = new Mp4VideoReader(File.OpenRead(Mp4FileSourceTest.H264AacFixture), leaveOpen: false, pool);

            using (var first = reader.ReadFrames().First())
            {
                first.Timestamp.Should().Be(TimeSpan.Zero);
            }

            pool.RetainedBytes.Should().BeGreaterThan(0);
        }

        private static List<VideoFrame> DecodeDirectly()
        {
            using var source = Mp4FileSource.Open(Mp4FileSourceTest.H264AacFixture);
            var track = source.Tracks.Tracks[0];
            using var decoder = new H264Decoder(track.CodecConfiguration.Span);
            var rotate = new RotateTransform(270);
            var frames = new List<VideoFrame>();
            foreach (var packet in source.ReadPackets().Where(p => p.TrackId == track.TrackId))
            {
                decoder.Decode(packet.Payload.Span, TimeSpan.FromTicks(MediaTimestamp.Rescale(packet.Pts, track.Timescale, 10_000_000)), f => rotate.Apply(f, frames.Add));
            }

            decoder.Flush(f => rotate.Apply(f, frames.Add));
            return frames;
        }

        private static VideoFrame ToFullRange(VideoFrame frame)
        {
            var full = new VideoFramePool().Rent(frame.Width, frame.Height, VideoColorRange.Full, VideoColorMatrix.Bt601);
            for (var y = 0; y < frame.Height; y++)
            {
                var source = frame.GetRow(VideoPlane.Y, y);
                var target = full.GetRow(VideoPlane.Y, y);
                for (var x = 0; x < frame.Width; x++)
                {
                    target[x] = (byte)Math.Clamp(Math.Round((source[x] - 16) * 255 / 219.0), 0, 255);
                }
            }

            return full;
        }
    }
}
