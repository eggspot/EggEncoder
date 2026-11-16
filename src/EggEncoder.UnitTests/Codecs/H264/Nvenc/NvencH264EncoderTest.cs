using EggEncoder.Codecs.H264;
using EggEncoder.Codecs.H264.Nvenc;
using EggEncoder.Containers.Mp4;
using EggEncoder.Media;
using EggEncoder.UnitTests.TestUtilities;
using EggEncoder.Video;
using EggEncoder.Video.Composition;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.H264.Nvenc
{
    public sealed class NvencH264EncoderTest
    {
        [Theory]
        [InlineData(H264Profile.High, 100)]
        [InlineData(H264Profile.Main, 77)]
        [InlineData(H264Profile.Baseline, 66)]
        public void Encoded_Frames_Should_Decode_Close_To_The_Source(H264Profile profile, int profileIdc)
        {
            if (!NvencH264Encoder.IsSupported)
            {
                return;
            }

            var frames = H264EncoderTest.SyntheticFrames(320, 240, 24);
            var settings = new H264EncoderSettings { Width = 320, Height = 240, FrameRate = new VideoFrameRate(24), Profile = profile, ConstantRateFactor = 20, KeyframeInterval = 10 };
            var encoded = new List<H264EncodedFrame>();
            AvcDecoderConfigurationRecord configuration;
            using (var encoder = new NvencH264Encoder(settings))
            {
                configuration = encoder.DecoderConfiguration;
                foreach (var frame in frames)
                {
                    encoder.Encode(frame, encoded.Add);
                }

                encoder.Flush(encoded.Add);
            }

            configuration.ProfileIndication.Should().Be(profileIdc);
            configuration.NalLengthSize.Should().Be(4);
            encoded.Should().HaveCount(frames.Count);
            encoded.Select((f, i) => (f.IsKeyframe, i)).Where(p => p.IsKeyframe).Select(p => p.i).Should().Equal(0, 10, 20);
            encoded.Select(f => f.Timestamp).Should().Equal(frames.Select(f => f.Timestamp));
            var decoded = new List<VideoFrame>();
            using (var decoder = new H264Decoder(configuration.ToArray(), maxDegreeOfParallelism: 1, pool: null))
            {
                foreach (var frame in encoded)
                {
                    decoder.Decode(frame.Payload.Span, frame.Timestamp, decoded.Add);
                }

                decoder.Flush(decoded.Add);
            }

            try
            {
                decoded.Should().HaveCount(frames.Count);
                for (var i = 0; i < frames.Count; i++)
                {
                    LumaPsnr(decoded[i], frames[i]).Should().BeGreaterThan(30, $"frame {i}");
                }
            }
            finally
            {
                decoded.ForEach(f => f.Dispose());
                frames.ForEach(f => f.Dispose());
            }
        }

        [Fact]
        public void Output_Should_Come_In_Order_At_Most_Three_Frames_Late()
        {
            if (!NvencH264Encoder.IsSupported)
            {
                return;
            }

            var frames = H264EncoderTest.SyntheticFrames(160, 96, 10);
            var received = new List<TimeSpan>();
            var lags = new List<int>();
            try
            {
                using var encoder = new NvencH264Encoder(new H264EncoderSettings { Width = 160, Height = 96, FrameRate = new VideoFrameRate(24) });
                for (var i = 0; i < frames.Count; i++)
                {
                    encoder.Encode(frames[i], f => received.Add(f.Timestamp));
                    lags.Add(i + 1 - received.Count);
                }

                encoder.Flush(f => received.Add(f.Timestamp));
            }
            finally
            {
                frames.ForEach(f => f.Dispose());
            }

            received.Should().Equal(frames.Select(f => f.Timestamp));
            lags.Should().OnlyContain(lag => lag >= 0 && lag <= 3);
        }

        [Fact]
        public void Lower_Quality_Should_Make_Smaller_Frames()
        {
            if (!NvencH264Encoder.IsSupported)
            {
                return;
            }

            var frames = H264EncoderTest.SyntheticFrames(320, 240, 12);
            try
            {
                Bytes(frames, 18).Should().BeGreaterThan(Bytes(frames, 35));
            }
            finally
            {
                frames.ForEach(f => f.Dispose());
            }
        }

        [Fact]
        public void A_Frame_Of_Another_Size_Should_Be_Refused_And_Dispose_Should_Be_Repeatable()
        {
            if (!NvencH264Encoder.IsSupported)
            {
                return;
            }

            var encoder = new NvencH264Encoder(new H264EncoderSettings { Width = 64, Height = 64, FrameRate = new VideoFrameRate(30) });
            using var frame = VideoFrame.Create(32, 32, VideoColorRange.Limited, VideoColorMatrix.Bt709);

            var act = () => encoder.Encode(frame, _ => { });

            act.Should().Throw<ArgumentException>();
            encoder.Dispose();
            encoder.Dispose();
            ((Action)(() => encoder.Encode(frame, _ => { }))).Should().Throw<ObjectDisposedException>();
        }

        [Fact]
        public void Odd_Sizes_And_Bad_Presets_Should_Be_Refused_Before_Touching_The_Gpu()
        {
            var odd = new H264EncoderSettings { Width = 63, Height = 64, FrameRate = new VideoFrameRate(30) };
            var even = odd with { Width = 64 };

            ((Action)(() => _ = new NvencH264Encoder(odd))).Should().Throw<ArgumentException>();
            ((Action)(() => _ = new NvencH264Encoder(even, preset: 0))).Should().Throw<ArgumentOutOfRangeException>();
            ((Action)(() => _ = new NvencH264Encoder(even, preset: 8))).Should().Throw<ArgumentOutOfRangeException>();
            ((Action)(() => _ = new NvencH264Encoder(even, deviceIndex: -1))).Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Without_A_Gpu_The_Constructor_Should_Throw_NotSupportedException()
        {
            if (NvencH264Encoder.IsSupported)
            {
                return;
            }

            var act = () => new NvencH264Encoder(new H264EncoderSettings { Width = 64, Height = 64, FrameRate = new VideoFrameRate(30) });

            act.Should().Throw<Exception>().Which.Should().BeOfType<NotSupportedException>();
        }

        [Fact]
        public void A_Composition_Rendered_With_Nvenc_Should_Decode()
        {
            if (!NvencH264Encoder.IsSupported)
            {
                return;
            }

            var directory = Directory.CreateTempSubdirectory("eggencoder-nvenc-").FullName;
            try
            {
                var clip = Path.Combine(directory, "clip.mp4");
                SyntheticClip.Write(clip, 160, 96, 30, 25, new RgbaColor(30, 60, 200).ToYuv(VideoColorMatrix.Bt709, VideoColorRange.Limited));
                var output = Path.Combine(directory, "out.mp4");

                var result = new VideoComposition
                {
                    Width = 160,
                    Height = 96,
                    FrameRate = new VideoFrameRate(25),
                    Scenes = [new CompositionScene { Source = clip }],
                    VideoEncoder = H264EncoderBackend.Nvenc
                }.Render(output);

                result.FrameCount.Should().Be(30);
                using var reader = Mp4VideoReader.Open(output);
                var frames = reader.ReadFrames().ToList();
                frames.Should().HaveCount(30);
                frames.ForEach(f => f.Dispose());
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private static long Bytes(List<VideoFrame> frames, double quality)
        {
            long total = 0;
            using var encoder = new NvencH264Encoder(new H264EncoderSettings { Width = 320, Height = 240, FrameRate = new VideoFrameRate(24), ConstantRateFactor = quality });
            foreach (var frame in frames)
            {
                encoder.Encode(frame, f => total += f.Payload.Length);
            }

            encoder.Flush(f => total += f.Payload.Length);
            return total;
        }

        private static double LumaPsnr(VideoFrame actual, VideoFrame expected)
        {
            double squared = 0;
            for (var y = 0; y < expected.Height; y++)
            {
                var a = actual.GetRow(VideoPlane.Y, y);
                var e = expected.GetRow(VideoPlane.Y, y);
                for (var x = 0; x < expected.Width; x++)
                {
                    var d = a[x] - e[x];
                    squared += d * d;
                }
            }

            var mse = squared / (expected.Width * expected.Height);
            return mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(255 * 255 / mse);
        }
    }
}
