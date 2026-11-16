using System.Security.Cryptography;
using EggEncoder.Codecs.H264;
using EggEncoder.Codecs.H264.Nvdec;
using EggEncoder.Containers.Mp4;
using EggEncoder.UnitTests.Containers.Mp4;
using EggEncoder.UnitTests.TestUtilities;
using EggEncoder.Video;
using EggEncoder.Video.Composition;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.H264.Nvdec
{
    public sealed class NvdecH264DecoderTest
    {
        [Fact]
        public void A_B_Frame_Mp4_Should_Decode_To_The_Same_Frames_As_The_Cpu_Decoder()
        {
            if (!NvdecH264Decoder.IsSupported)
            {
                return;
            }

            var cpu = Frames(Mp4FileSourceTest.H264AacFixture, H264DecoderBackend.Software);
            var gpu = Frames(Mp4FileSourceTest.H264AacFixture, H264DecoderBackend.Nvdec);

            cpu.Should().NotBeEmpty();
            gpu.Should().Equal(cpu);
        }

        [Fact]
        public void Our_Own_Encoder_Output_Should_Decode_To_The_Same_Frames_As_The_Cpu_Decoder()
        {
            if (!NvdecH264Decoder.IsSupported)
            {
                return;
            }

            var directory = Directory.CreateTempSubdirectory("eggencoder-nvdec-").FullName;
            try
            {
                var clip = Path.Combine(directory, "clip.mp4");
                SyntheticClip.Write(clip, 66, 50, 40, 25, new RgbaColor(200, 60, 30).ToYuv(VideoColorMatrix.Bt709, VideoColorRange.Limited), keyframeInterval: 9);

                Frames(clip, H264DecoderBackend.Nvdec).Should().Equal(Frames(clip, H264DecoderBackend.Software)).And.HaveCount(40);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void A_Seek_Should_Start_At_The_Same_Frame_As_The_Cpu_Decoder()
        {
            if (!NvdecH264Decoder.IsSupported)
            {
                return;
            }

            var start = TimeSpan.FromSeconds(1);

            Frames(Mp4FileSourceTest.H264AacFixture, H264DecoderBackend.Nvdec, start).Should().Equal(Frames(Mp4FileSourceTest.H264AacFixture, H264DecoderBackend.Software, start));
        }

        [Fact]
        public void A_Composition_Decoded_On_The_Gpu_Should_Write_The_Same_File()
        {
            if (!NvdecH264Decoder.IsSupported)
            {
                return;
            }

            var directory = Directory.CreateTempSubdirectory("eggencoder-nvdec-").FullName;
            try
            {
                var composition = new VideoComposition
                {
                    Width = 96,
                    Height = 64,
                    FrameRate = new VideoFrameRate(25),
                    Scenes = [new CompositionScene { Source = Mp4FileSourceTest.H264AacFixture, Duration = TimeSpan.FromSeconds(1) }]
                };
                var cpu = Path.Combine(directory, "cpu.mp4");
                var gpu = Path.Combine(directory, "gpu.mp4");

                composition.Render(cpu);
                (composition with { VideoDecoder = H264DecoderBackend.Nvdec }).Render(gpu);

                File.ReadAllBytes(gpu).Should().Equal(File.ReadAllBytes(cpu));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void Without_A_Gpu_The_Constructor_Should_Throw_NotSupportedException()
        {
            if (NvdecH264Decoder.IsSupported)
            {
                return;
            }

            using var reader = Mp4VideoReader.Open(Mp4FileSourceTest.H264AacFixture, null, H264DecoderBackend.Nvdec);

            var act = () => reader.ReadFrames().First().Dispose();

            act.Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void An_Unknown_Decoder_Should_Be_Refused()
        {
            var act = () => Mp4VideoReader.Open(Mp4FileSourceTest.H264AacFixture, null, (H264DecoderBackend)9);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        private static List<string> Frames(string path, H264DecoderBackend decoder, TimeSpan start = default)
        {
            using var reader = Mp4VideoReader.Open(path, null, decoder);
            var frames = new List<string>();
            foreach (var frame in reader.ReadFrames(start))
            {
                using (frame)
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    for (var y = 0; y < frame.Height; y++)
                    {
                        hash.AppendData(frame.GetRow(VideoPlane.Y, y));
                    }

                    for (var y = 0; y < frame.ChromaHeight; y++)
                    {
                        hash.AppendData(frame.GetRow(VideoPlane.U, y));
                        hash.AppendData(frame.GetRow(VideoPlane.V, y));
                    }

                    frames.Add($"{frame.Timestamp.Ticks} {frame.Width}x{frame.Height} {frame.Range} {frame.Matrix} {Convert.ToHexString(hash.GetHashAndReset())}");
                }
            }

            return frames;
        }
    }
}
