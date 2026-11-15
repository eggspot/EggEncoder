using System.Diagnostics;
using EggEncoder.Codecs.H264;
using EggEncoder.Media;
using EggEncoder.Transform;
using EggEncoder.Video;
using FluentAssertions;
using static EggEncoder.UnitTests.Codecs.H264.H264TestStreams;

namespace EggEncoder.UnitTests.Codecs.H264
{
    public class H264EncoderTest
    {
        private static readonly VideoFramePool Pool = new();

        [Fact]
        public void Encode_1080x1920_On_Four_Threads_Should_Run_In_Real_Time()
        {
            var path = Environment.GetEnvironmentVariable("EGGENCODER_H264_ENCODE_TARGET_FILE");
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return;
            }

            var data = File.ReadAllBytes(path);
            const int width = 1080;
            const int height = 1920;
            var frameBytes = width * height * 3 / 2;
            var frames = new List<VideoFrame>();
            for (var offset = 0; offset + frameBytes <= data.Length; offset += frameBytes)
            {
                var frame = Pool.Rent(width, height);
                var position = offset;
                for (var y = 0; y < height; y++, position += width)
                {
                    data.AsSpan(position, width).CopyTo(frame.GetRow(VideoPlane.Y, y));
                }

                foreach (var plane in new[] { VideoPlane.U, VideoPlane.V })
                {
                    for (var y = 0; y < height / 2; y++, position += width / 2)
                    {
                        data.AsSpan(position, width / 2).CopyTo(frame.GetRow(plane, y));
                    }
                }

                frames.Add(frame);
            }

            using var encoder = new H264Encoder(new H264EncoderSettings { Width = width, Height = height, FrameRate = new VideoFrameRate(30), Profile = H264Profile.High, MaxDegreeOfParallelism = 4 });
            var watch = Stopwatch.StartNew();
            foreach (var frame in frames)
            {
                encoder.Encode(frame, _ => { });
            }

            watch.Stop();
            frames.ForEach(f => f.Dispose());
            (frames.Count / watch.Elapsed.TotalSeconds).Should().BeGreaterThanOrEqualTo(30);
        }

        [Theory]
        [InlineData(64, 48, H264Profile.Baseline, 18)]
        [InlineData(64, 48, H264Profile.High, 30)]
        [InlineData(66, 50, H264Profile.Main, 23)]
        [InlineData(176, 144, H264Profile.High, 26)]
        [InlineData(16, 16, H264Profile.Baseline, 40)]
        [InlineData(96, 64, H264Profile.High, 4)]
        public void Encoded_Stream_Should_Decode_To_The_Encoder_Reconstruction(int width, int height, H264Profile profile, double rateFactor)
        {
            var settings = new H264EncoderSettings
            {
                Width = width,
                Height = height,
                FrameRate = new VideoFrameRate(25),
                ConstantRateFactor = rateFactor,
                KeyframeInterval = 6,
                Profile = profile
            };

            var (stream, reconstructions, keyframes) = Encode(settings, SyntheticFrames(width, height, 10));

            keyframes.Should().Equal(true, false, false, false, false, false, true, false, false, false);
            DecodeToMd5(stream).Should().Equal(reconstructions);
        }

        [Theory]
        [InlineData(H264Profile.Baseline, 66, 50)]
        [InlineData(H264Profile.High, 176, 144)]
        public void Ffmpeg_Should_Decode_The_Output_To_The_Encoder_Reconstruction(H264Profile profile, int width, int height)
        {
            if (Environment.GetEnvironmentVariable("EGGENCODER_FFMPEG_ORACLE") != "1")
            {
                return;
            }

            var settings = new H264EncoderSettings { Width = width, Height = height, FrameRate = new VideoFrameRate(25), Profile = profile, KeyframeInterval = 5 };
            var (stream, reconstructions, _) = Encode(settings, SyntheticFrames(width, height, 8));
            var path = Path.Combine(Path.GetTempPath(), $"eggencoder-h264-{Guid.NewGuid():N}.h264");
            try
            {
                File.WriteAllBytes(path, stream);
                var startInfo = new ProcessStartInfo("ffmpeg", $"-v error -i \"{path}\" -f framemd5 -pix_fmt yuv420p -")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                };
                using var process = Process.Start(startInfo)!;
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                process.ExitCode.Should().Be(0);
                output.Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split(',')[^1].Trim()).Should().Equal(reconstructions);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Quality_Should_Stay_Above_A_Psnr_Floor_And_Rise_With_A_Lower_Rate_Factor()
        {
            var frames = SyntheticFrames(176, 144, 8);
            var high = Psnr(new H264EncoderSettings { Width = 176, Height = 144, FrameRate = new VideoFrameRate(25), ConstantRateFactor = 18 }, frames);
            var low = Psnr(new H264EncoderSettings { Width = 176, Height = 144, FrameRate = new VideoFrameRate(25), ConstantRateFactor = 34 }, frames);

            high.Should().BeGreaterThan(40);
            low.Should().BeGreaterThan(22);
            high.Should().BeGreaterThan(low + 4);
        }

        [Fact]
        public void Encoding_The_Same_Frames_Twice_Should_Produce_The_Same_Bytes()
        {
            var frames = SyntheticFrames(176, 144, 6);
            var settings = new H264EncoderSettings { Width = 176, Height = 144, FrameRate = new VideoFrameRate(25), ConstantRateFactor = 26 };

            Encode(settings, frames).Stream.Should().Equal(Encode(settings, frames).Stream);
        }

        [Theory]
        [InlineData(H264Profile.Baseline)]
        [InlineData(H264Profile.High)]
        public void Wavefront_Threads_Should_Not_Change_The_Output(H264Profile profile)
        {
            var frames = SyntheticFrames(176, 144, 6);
            var settings = new H264EncoderSettings { Width = 176, Height = 144, FrameRate = new VideoFrameRate(25), Profile = profile };

            var single = Encode(settings with { MaxDegreeOfParallelism = 1 }, frames);
            var threaded = Encode(settings with { MaxDegreeOfParallelism = 4 }, frames);

            threaded.Stream.Should().Equal(single.Stream);
            DecodeToMd5(threaded.Stream).Should().Equal(single.Reconstructions);
        }

        [Theory]
        [InlineData(H264Profile.Baseline, 1)]
        [InlineData(H264Profile.High, 1)]
        [InlineData(H264Profile.High, 3)]
        [InlineData(H264Profile.Main, 8)]
        public void Delayed_Frames_Should_Come_Out_In_Order_With_The_Same_Bytes(H264Profile profile, int delayedFrames)
        {
            var frames = SyntheticFrames(176, 144, 10);
            for (var i = 0; i < frames.Count; i++)
            {
                frames[i].Timestamp = TimeSpan.FromMilliseconds(40 * i);
            }

            var settings = new H264EncoderSettings { Width = 176, Height = 144, FrameRate = new VideoFrameRate(25), Profile = profile, MaxDegreeOfParallelism = 4, KeyframeInterval = 4 };
            var immediate = EncodeWithDelay(settings, frames);
            var delayed = EncodeWithDelay(settings with { MaxDelayedFrames = delayedFrames }, frames);

            delayed.Stream.Should().Equal(immediate.Stream);
            delayed.Timestamps.Should().Equal(frames.Select(f => f.Timestamp));
            delayed.Keyframes.Should().Equal(immediate.Keyframes);
            for (var call = 0; call < frames.Count; call++)
            {
                immediate.OutputAfterCall[call].Should().Be(call + 1);
                delayed.OutputAfterCall[call].Should().BeGreaterThanOrEqualTo(call + 1 - delayedFrames);
            }
        }

        [Fact]
        public void An_Encoder_Disposed_With_Frames_Held_Back_Should_Release_Them()
        {
            var frames = SyntheticFrames(176, 144, 3);
            var encoder = new H264Encoder(new H264EncoderSettings { Width = 176, Height = 144, FrameRate = new VideoFrameRate(25), MaxDelayedFrames = 3 });
            foreach (var frame in frames)
            {
                encoder.Encode(frame, _ => { });
            }

            encoder.Invoking(e => e.Dispose()).Should().NotThrow();
            encoder.Invoking(e => e.Flush(_ => { })).Should().Throw<ObjectDisposedException>();
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(H264EncoderSettings.MaxDelayedFramesLimit + 1)]
        public void Out_Of_Range_Delayed_Frames_Should_Be_Refused(int delayedFrames)
        {
            var settings = new H264EncoderSettings { Width = 64, Height = 48, FrameRate = new VideoFrameRate(25), MaxDelayedFrames = delayedFrames };

            FluentActions.Invoking(() => new H264Encoder(settings)).Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Lower_Rate_Factor_Should_Produce_A_Larger_Stream()
        {
            var frames = SyntheticFrames(176, 144, 8);
            var small = Encode(new H264EncoderSettings { Width = 176, Height = 144, FrameRate = new VideoFrameRate(25), ConstantRateFactor = 34 }, frames).Stream.Length;
            var large = Encode(new H264EncoderSettings { Width = 176, Height = 144, FrameRate = new VideoFrameRate(25), ConstantRateFactor = 18 }, frames).Stream.Length;

            large.Should().BeGreaterThan(small * 2);
        }

        [Fact]
        public void Decoder_Configuration_Should_Describe_Profile_Level_Size_And_Timing()
        {
            using var encoder = new H264Encoder(new H264EncoderSettings
            {
                Width = 1080,
                Height = 1920,
                FrameRate = new VideoFrameRate(30000, 1001),
                ColorRange = VideoColorRange.Full,
                Profile = H264Profile.High
            });

            var configuration = encoder.DecoderConfiguration;
            var info = H264SpsParser.Parse(configuration.SequenceParameterSets[0].Span);
            info.Should().Be(new H264SpsInfo(100, 0, 40, 1, 8, 8, 1080, 1920));
            configuration.NalLengthSize.Should().Be(4);
            AvcDecoderConfigurationRecord.Parse(configuration.ToArray()).LevelIndication.Should().Be(40);
            var sps = SequenceParameterSet.Parse(H264BitReader.FromNalPayload(configuration.SequenceParameterSets[0].Span[1..]));
            sps.FrameRate.Should().Be(new VideoFrameRate(30000, 1001));
            sps.Range.Should().Be(VideoColorRange.Full);
            sps.ColorMatrix.Should().Be(VideoColorMatrix.Bt709);
            sps.MaxNumberOfReorderFrames.Should().Be(0);
            sps.Height.Should().Be(1920);
        }

        [Theory]
        [InlineData(4, 3, 30.0, 10)]
        [InlineData(11, 9, 30.0, 11)]
        [InlineData(22, 18, 30.0, 13)]
        [InlineData(80, 45, 30.0, 31)]
        [InlineData(68, 120, 30.0, 40)]
        [InlineData(120, 68, 60.0, 42)]
        [InlineData(240, 135, 30.0, 51)]
        public void ChooseLevel_Should_Pick_The_Smallest_Level_That_Holds_The_Stream(int widthInMacroblocks, int heightInMacroblocks, double fps, int expected)
        {
            H264Encoder.ChooseLevel(widthInMacroblocks, heightInMacroblocks, new VideoFrameRate((int)fps), 1).Should().Be(expected);
        }

        [Fact]
        public void Odd_Sizes_And_Mismatched_Frames_Should_Be_Refused()
        {
            var settings = new H264EncoderSettings { Width = 63, Height = 48, FrameRate = new VideoFrameRate(25) };
            FluentActions.Invoking(() => new H264Encoder(settings)).Should().Throw<ArgumentException>();
            FluentActions.Invoking(() => new H264Encoder(settings with { Width = 64, ConstantRateFactor = 52 })).Should().Throw<ArgumentException>();
            FluentActions.Invoking(() => new H264Encoder(settings with { Width = 9000 })).Should().Throw<ArgumentException>();

            using var encoder = new H264Encoder(settings with { Width = 64 });
            using var frame = Pool.Rent(32, 32);
            encoder.Invoking(e => e.Encode(frame, _ => { })).Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Cavlc_Writer_Should_Write_What_The_Decoder_Reads()
        {
            var random = new Random(3);
            foreach (var maxNumCoeff in new[] { 4, 15, 16 })
            {
                foreach (var nC in maxNumCoeff == 4 ? new[] { -1 } : new[] { 0, 1, 2, 3, 4, 7, 8, 16 })
                {
                    for (var round = 0; round < 200; round++)
                    {
                        var block = new short[16];
                        var density = random.Next(1, 100);
                        for (var i = 0; i < maxNumCoeff; i++)
                        {
                            if (random.Next(100) < density)
                            {
                                var magnitude = random.Next(10) switch
                                {
                                    < 5 => 1,
                                    < 8 => random.Next(2, 20),
                                    < 9 => random.Next(20, 3000),
                                    _ => random.Next(3000, 32000)
                                };
                                block[i] = (short)(random.Next(2) == 0 ? magnitude : -magnitude);
                            }
                        }

                        var writer = new BitWriter();
                        var written = CavlcWriter.WriteBlock(writer, nC, maxNumCoeff, block);
                        writer.WriteBits(1, 1);
                        writer.ByteAlign();
                        var read = new short[16];
                        var total = CavlcResidual.ReadBlock(new H264BitReader(writer.ToArray()), nC, maxNumCoeff, read);

                        total.Should().Be(written);
                        read.Should().Equal(block);
                    }
                }
            }
        }

        [Fact]
        public void Satd_Should_Equal_The_Sum_Of_Hadamard_Magnitudes()
        {
            var random = new Random(9);
            foreach (var (width, height) in new[] { (16, 16), (8, 8), (4, 4), (16, 8) })
            {
                for (var round = 0; round < 50; round++)
                {
                    var source = new byte[32 * 20];
                    var prediction = new byte[24 * 20];
                    random.NextBytes(source);
                    random.NextBytes(prediction);
                    var expected = 0;
                    for (var by = 0; by < height; by += 4)
                    {
                        for (var bx = 0; bx < width; bx += 4)
                        {
                            var d = new int[4, 4];
                            for (var y = 0; y < 4; y++)
                            {
                                for (var x = 0; x < 4; x++)
                                {
                                    d[y, x] = source[((by + y) * 32) + bx + x] - prediction[((by + y) * 24) + bx + x];
                                }
                            }

                            int[] h = [1, 1, 1, 1, 1, -1, 1, -1, 1, 1, -1, -1, 1, -1, -1, 1];
                            for (var u = 0; u < 4; u++)
                            {
                                for (var v = 0; v < 4; v++)
                                {
                                    var sum = 0;
                                    for (var y = 0; y < 4; y++)
                                    {
                                        for (var x = 0; x < 4; x++)
                                        {
                                            sum += h[(u * 4) + y] * h[(v * 4) + x] * d[y, x];
                                        }
                                    }

                                    expected += Math.Abs(sum);
                                }
                            }
                        }
                    }

                    BlockCost.Satd(source, 32, prediction, 24, width, height).Should().Be(expected >> 1);
                }
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Transform_And_Quantisation_Should_Round_Trip_Through_The_Decoder_At_Low_Qp(bool eightByEight)
        {
            var random = new Random(5);
            var dequantizer = new Dequantizer(ScalingLists.FlatSet());
            var size = eightByEight ? 8 : 4;
            for (var round = 0; round < 200; round++)
            {
                var residual = new int[size * size];
                for (var i = 0; i < residual.Length; i++)
                {
                    residual[i] = random.Next(-60, 61);
                }

                var coefficients = new int[64];
                var raster = new short[64];
                var scanned = new short[64];
                var dequantized = new int[64];
                var plane = new byte[size * size];
                Array.Fill(plane, (byte)128);
                if (eightByEight)
                {
                    ForwardTransform.Core8x8(residual, coefficients);
                    Quantizer.Quantize8x8(coefficients, raster, scanned, 4, intra: false);
                    dequantizer.Dequantize8x8(raster, dequantized, 4, 1);
                    InverseTransform.Add8x8(dequantized, plane, 0, 8);
                }
                else
                {
                    ForwardTransform.Core4x4(residual, coefficients);
                    Quantizer.Quantize4x4(coefficients, raster, scanned, 4, intra: false, 0);
                    dequantizer.Dequantize4x4(raster, dequantized, 4, 3, keepDc: false);
                    InverseTransform.Add4x4(dequantized, plane, 0, 4);
                }

                for (var i = 0; i < residual.Length; i++)
                {
                    (plane[i] - 128 - residual[i]).Should().BeInRange(-2, 2);
                }
            }
        }

        [Fact]
        public void Exp_Golomb_Lengths_Should_Match_What_Is_Written()
        {
            foreach (var value in new[] { 0, 1, 2, 3, 6, 7, 100, 65535, 1 << 20 })
            {
                var writer = new BitWriter();
                writer.WriteUe((uint)value);
                writer.BitPosition.Should().Be(ExpGolombWriter.UeLength((uint)value));
                new H264BitReader([.. writer.ToArray(), 0x80]).ReadUe().Should().Be((uint)value);
            }

            foreach (var value in new[] { 0, 1, -1, 5, -5, 2047, -2048 })
            {
                var writer = new BitWriter();
                writer.WriteSe(value);
                writer.BitPosition.Should().Be(ExpGolombWriter.SeLength(value));
                new H264BitReader([.. writer.ToArray(), 0x80]).ReadSe().Should().Be(value);
            }
        }

        private static (byte[] Stream, List<string> Reconstructions, List<bool> Keyframes) Encode(H264EncoderSettings settings, List<VideoFrame> frames)
        {
            using var encoder = new H264Encoder(settings);
            var stream = new List<byte>();
            var configuration = encoder.DecoderConfiguration;
            foreach (var nal in configuration.SequenceParameterSets.Concat(configuration.PictureParameterSets))
            {
                stream.AddRange([0, 0, 0, 1]);
                stream.AddRange(nal.ToArray());
            }

            var reconstructions = new List<string>();
            var keyframes = new List<bool>();
            foreach (var frame in frames)
            {
                encoder.Encode(frame, encoded =>
                {
                    stream.AddRange(AnnexBConverter.ToAnnexB(encoded.Payload.Span));
                    keyframes.Add(encoded.IsKeyframe);
                    reconstructions.Add(CroppedMd5(encoder.LastReconstruction!, settings.Width, settings.Height));
                });
            }

            return ([.. stream], reconstructions, keyframes);
        }

        private static (byte[] Stream, List<TimeSpan> Timestamps, List<bool> Keyframes, List<int> OutputAfterCall) EncodeWithDelay(H264EncoderSettings settings, List<VideoFrame> frames)
        {
            using var encoder = new H264Encoder(settings);
            var stream = new List<byte>();
            var timestamps = new List<TimeSpan>();
            var keyframes = new List<bool>();
            var outputAfterCall = new List<int>();
            void Collect(H264EncodedFrame encoded)
            {
                stream.AddRange(AnnexBConverter.ToAnnexB(encoded.Payload.Span));
                timestamps.Add(encoded.Timestamp);
                keyframes.Add(encoded.IsKeyframe);
            }

            foreach (var frame in frames)
            {
                encoder.Encode(frame, Collect);
                outputAfterCall.Add(timestamps.Count);
            }

            encoder.Flush(Collect);
            return ([.. stream], timestamps, keyframes, outputAfterCall);
        }

        private static double Psnr(H264EncoderSettings settings, List<VideoFrame> frames)
        {
            using var encoder = new H264Encoder(settings);
            double totalError = 0;
            long samples = 0;
            foreach (var frame in frames)
            {
                encoder.Encode(frame, _ =>
                {
                    var reconstruction = encoder.LastReconstruction!.Frame;
                    for (var y = 0; y < frame.Height; y++)
                    {
                        var a = frame.GetRow(VideoPlane.Y, y);
                        var b = reconstruction.GetRow(VideoPlane.Y, y);
                        for (var x = 0; x < frame.Width; x++)
                        {
                            var d = a[x] - b[x];
                            totalError += d * d;
                            samples++;
                        }
                    }
                });
            }

            return 10 * Math.Log10(255.0 * 255.0 / (totalError / samples));
        }

        private static string CroppedMd5(H264Picture picture, int width, int height)
        {
            using var frame = Pool.Rent(width, height);
            for (var y = 0; y < height; y++)
            {
                picture.Frame.GetRow(VideoPlane.Y, y)[..width].CopyTo(frame.GetRow(VideoPlane.Y, y));
            }

            foreach (var plane in new[] { VideoPlane.U, VideoPlane.V })
            {
                for (var y = 0; y < frame.ChromaHeight; y++)
                {
                    picture.Frame.GetRow(plane, y)[..frame.ChromaWidth].CopyTo(frame.GetRow(plane, y));
                }
            }

            return Md5(frame);
        }

        internal static List<VideoFrame> SyntheticFrames(int width, int height, int count)
        {
            var random = new Random(width * 31 + height);
            var frames = new List<VideoFrame>();
            for (var index = 0; index < count; index++)
            {
                var frame = Pool.Rent(width, height);
                for (var y = 0; y < height; y++)
                {
                    var row = frame.GetRow(VideoPlane.Y, y);
                    for (var x = 0; x < width; x++)
                    {
                        var value = ((x + (2 * index)) * 3) + (y * 2) + random.Next(-6, 7);
                        var inBox = x >= 8 + (3 * index) && x < 24 + (3 * index) && y >= 6 + index && y < 22 + index;
                        row[x] = (byte)Math.Clamp(inBox ? 230 - value / 3 : value % 256, 0, 255);
                    }
                }

                for (var y = 0; y < frame.ChromaHeight; y++)
                {
                    var u = frame.GetRow(VideoPlane.U, y);
                    var v = frame.GetRow(VideoPlane.V, y);
                    for (var x = 0; x < frame.ChromaWidth; x++)
                    {
                        u[x] = (byte)Math.Clamp(128 + ((x - y + index) * 2), 0, 255);
                        v[x] = (byte)Math.Clamp(100 + (x * 3) + (y % 7), 0, 255);
                    }
                }

                frame.Timestamp = TimeSpan.FromMilliseconds(40 * index);
                frames.Add(frame);
            }

            return frames;
        }
    }
}
