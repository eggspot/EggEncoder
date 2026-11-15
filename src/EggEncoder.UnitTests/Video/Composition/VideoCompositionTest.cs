using System.Diagnostics;
using EggEncoder.Containers.Mp4;
using EggEncoder.Media;
using EggEncoder.Pcm;
using EggEncoder.UnitTests.Containers.Mp4;
using EggEncoder.UnitTests.TestUtilities;
using EggEncoder.UnitTests.Video.Text;
using EggEncoder.Video;
using EggEncoder.Video.Composition;
using EggEncoder.Video.Filters;
using EggEncoder.Video.Text;
using FluentAssertions;

namespace EggEncoder.UnitTests.Video.Composition
{
    public sealed class VideoCompositionTest : IDisposable
    {
        private static readonly YuvColor Blue = new RgbaColor(30, 60, 200).ToYuv(VideoColorMatrix.Bt709, VideoColorRange.Limited);
        private static readonly YuvColor Green = new RgbaColor(40, 190, 70).ToYuv(VideoColorMatrix.Bt709, VideoColorRange.Limited);

        private readonly string _directory = Directory.CreateTempSubdirectory("eggencoder-composition-").FullName;

        public void Dispose() => Directory.Delete(_directory, recursive: true);

        [Fact]
        public void Render_Two_Scenes_Should_Write_Every_Planned_Frame_With_Audio()
        {
            var tone = Clip("tone.mp4", 12, Blue, toneHz: 440);
            var silent = Clip("silent.mp4", 12, Green);
            var composition = new VideoComposition
            {
                Width = 64,
                Height = 64,
                FrameRate = new VideoFrameRate(25),
                Scenes =
                [
                    new CompositionScene { Source = tone },
                    new CompositionScene { Source = silent, Start = TimeSpan.FromSeconds(0.08), Duration = TimeSpan.FromSeconds(0.32) }
                ],
                MaxDegreeOfParallelism = 2
            };
            var output = Path.Combine(_directory, "out.mp4");

            var result = composition.Render(output);

            result.FrameCount.Should().Be(20);
            result.Duration.Should().Be(TimeSpan.FromSeconds(0.8));
            result.HasAudio.Should().BeTrue();
            result.Width.Should().Be(64);
            result.Height.Should().Be(64);
            result.IntegratedLoudness.Should().BeApproximately(-14, 1.0);
            using (var source = Mp4FileSource.Open(output))
            {
                source.Tracks.Tracks.Select(t => t.Codec).Should().Equal(MediaCodec.H264, MediaCodec.Aac);
                source.GetSampleCount(source.Tracks.Tracks[0].TrackId).Should().Be(20);
            }

            using var reader = Mp4VideoReader.Open(output);
            var frames = reader.ReadFrames().ToList();
            try
            {
                frames.Should().HaveCount(20);
                frames.Select(f => f.Timestamp).Should().Equal(Enumerable.Range(0, 20).Select(i => TimeSpan.FromSeconds(i * 0.04)));
                MeanChroma(frames[5]).Should().BeCloseTo(Blue.U, 6);
                MeanChroma(frames[15]).Should().BeCloseTo(Green.U, 6);
            }
            finally
            {
                frames.ForEach(f => f.Dispose());
            }

            var audio = PcmAudio.Decode(output);
            audio.Duration.Should().BeCloseTo(TimeSpan.FromSeconds(0.8), TimeSpan.FromMilliseconds(25));
            Rms(audio, 0.05, 0.4).Should().BeGreaterThan(Rms(audio, 0.5, 0.75) * 10);
        }

        [Fact]
        public void Render_Without_Any_Sound_Should_Write_Video_Only()
        {
            var composition = new VideoComposition
            {
                Width = 32,
                Height = 48,
                FrameRate = new VideoFrameRate(25),
                Scenes = [new CompositionScene { Source = Clip("a.mp4", 5, Blue), Fit = FitMode.Contain }]
            };
            using var output = new MemoryStream();

            var result = composition.Render(output);

            result.HasAudio.Should().BeFalse();
            result.IntegratedLoudness.Should().BeNull();
            output.Position = 0;
            using var source = new Mp4FileSource(output);
            source.Tracks.Tracks.Should().ContainSingle(t => t.Kind == MediaKind.Video && t.Width == 32 && t.Height == 48);
        }

        [Fact]
        public void Render_Music_Should_Loop_Under_Silent_Clips_And_Duck_Under_Sound()
        {
            var music = Path.Combine(_directory, "music.wav");
            SyntheticClip.WriteTone(music, 0.3, 220);
            var composition = new VideoComposition
            {
                Width = 32,
                Height = 32,
                FrameRate = new VideoFrameRate(25),
                Scenes =
                [
                    new CompositionScene { Source = Clip("silent.mp4", 20, Green) },
                    new CompositionScene { Source = Clip("tone.mp4", 20, Blue, toneHz: 880) }
                ],
                Music = new CompositionMusic { Source = music, Volume = 0.5, Ducking = new SidechainCompressorSettings { Threshold = 0.05, Ratio = 8 } },
                TargetLoudnessLufs = null
            };
            var output = Path.Combine(_directory, "music.mp4");

            var result = composition.Render(output);

            result.HasAudio.Should().BeTrue();
            var audio = PcmAudio.Decode(output);
            Rms(audio, 0.4, 0.75).Should().BeGreaterThan(0.03);
            var bedWithoutDucking = Rms(audio, 0.1, 0.7);
            bedWithoutDucking.Should().BeGreaterThan(0.03);
        }

        [Fact]
        public void Render_End_Card_And_Subtitles_Should_Draw_Text()
        {
            var composition = new VideoComposition
            {
                Width = 96,
                Height = 64,
                FrameRate = new VideoFrameRate(25),
                Scenes = [new CompositionScene { Source = Clip("a.mp4", 10, Blue) }],
                EndCard = new CompositionEndCard { Text = "End", Duration = TimeSpan.FromSeconds(0.2), Style = new TextStyle { FontSize = 24 }, Background = new YuvColor(16, 128, 128) },
                Subtitles = [new SubtitleCue(TimeSpan.Zero, TimeSpan.FromSeconds(0.2), "Hi")],
                SubtitleStyle = new SubtitleStyle { FontSize = 120 },
                Font = TrueTypeFontTest.Noto()
            };
            var withText = Path.Combine(_directory, "card.mp4");
            var withoutText = Path.Combine(_directory, "plain.mp4");

            var result = composition.Render(withText);
            (composition with { Subtitles = [], EndCard = null }).Render(withoutText);

            result.FrameCount.Should().Be(15);
            var text = Decode(withText);
            var plain = Decode(withoutText);
            try
            {
                plain.Should().HaveCount(10);
                MaxLuma(text[13]).Should().BeGreaterThan(180);
                MinLuma(text[13]).Should().BeLessThan(30);
                ChangedPixels(text[1], plain[1]).Should().BeGreaterThan(20);
                ChangedPixels(text[8], plain[8]).Should().BeLessThan(5);
            }
            finally
            {
                text.ForEach(f => f.Dispose());
                plain.ForEach(f => f.Dispose());
            }
        }

        private static List<VideoFrame> Decode(string path)
        {
            using var reader = Mp4VideoReader.Open(path);
            return reader.ReadFrames().ToList();
        }

        private static int ChangedPixels(VideoFrame first, VideoFrame second)
        {
            var changed = 0;
            for (var y = 0; y < first.Height; y++)
            {
                var a = first.GetRow(VideoPlane.Y, y);
                var b = second.GetRow(VideoPlane.Y, y);
                for (var x = 0; x < first.Width; x++)
                {
                    if (Math.Abs(a[x] - b[x]) > 40)
                    {
                        changed++;
                    }
                }
            }

            return changed;
        }

        [Fact]
        public void Render_Sources_Without_An_Extension_Should_Be_Sniffed()
        {
            var copy = Path.Combine(_directory, "clip-without-extension");
            File.Copy(Mp4FileSourceTest.H264AacFixture, copy);
            var composition = new VideoComposition
            {
                Width = 48,
                Height = 64,
                FrameRate = new VideoFrameRate(25),
                Scenes = [new CompositionScene { Source = copy }]
            };

            var result = composition.Render(new MemoryStream());

            result.FrameCount.Should().Be(10);
            result.HasAudio.Should().BeTrue();
        }

        [Fact]
        public void Render_Thumbnail_Should_Come_From_The_Frame_At_The_Position_Or_The_Last()
        {
            var composition = new VideoComposition
            {
                Width = 32,
                Height = 32,
                FrameRate = new VideoFrameRate(25),
                Scenes =
                [
                    new CompositionScene { Source = Clip("blue.mp4", 5, Blue) },
                    new CompositionScene { Source = Clip("green.mp4", 5, Green) }
                ]
            };

            var early = composition.Render(new MemoryStream());
            var late = (composition with { ThumbnailAt = TimeSpan.FromSeconds(0.3) }).Render(new MemoryStream());
            var past = (composition with { ThumbnailAt = TimeSpan.FromSeconds(30) }).Render(new MemoryStream());

            var blue = YuvColor.FromRgb(30, 60, 200, VideoColorMatrix.Bt601, VideoColorRange.Full);
            var green = YuvColor.FromRgb(40, 190, 70, VideoColorMatrix.Bt601, VideoColorRange.Full);
            BaselineJpegDecoder.Decode(early.Thumbnail).Planes[1].Average(v => v).Should().BeApproximately(blue.U, 8);
            BaselineJpegDecoder.Decode(late.Thumbnail).Planes[1].Average(v => v).Should().BeApproximately(green.U, 8);
            BaselineJpegDecoder.Decode(past.Thumbnail).Planes[1].Average(v => v).Should().BeApproximately(green.U, 8);
        }

        [Fact]
        public void Render_Should_Report_Progress_Up_To_One()
        {
            var reports = new List<double>();
            var composition = new VideoComposition
            {
                Width = 32,
                Height = 32,
                FrameRate = new VideoFrameRate(25),
                Scenes = [new CompositionScene { Source = Clip("a.mp4", 6, Blue) }]
            };

            composition.Render(new MemoryStream(), new SynchronousProgress(reports.Add));

            reports.Should().BeInAscendingOrder();
            reports[^1].Should().Be(1);
            reports.Should().HaveCountGreaterThan(5);
        }

        [Fact]
        public async Task RenderAsync_Cancelled_Should_Throw_OperationCanceledException()
        {
            var composition = new VideoComposition
            {
                Width = 32,
                Height = 32,
                Scenes = [new CompositionScene { Source = Clip("a.mp4", 6, Blue) }]
            };
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var act = () => composition.RenderAsync(new MemoryStream(), cancellationToken: cancellation.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
            ((Action)(() => composition.Render(new MemoryStream(), cancellationToken: cancellation.Token))).Should().Throw<OperationCanceledException>();
        }

        [Fact]
        public void Render_Invalid_Compositions_Should_Throw_Before_Writing()
        {
            var clip = Clip("a.mp4", 5, Blue);
            var valid = new VideoComposition { Width = 32, Height = 32, Scenes = [new CompositionScene { Source = clip }] };
            var invalid = new[]
            {
                valid with { Width = 8 },
                valid with { Height = 5000 },
                valid with { Scenes = [] },
                valid with { Scenes = [new CompositionScene { Source = clip, Start = TimeSpan.FromSeconds(-1) }] },
                valid with { Scenes = [new CompositionScene { Source = clip, Duration = TimeSpan.Zero }] },
                valid with { Scenes = [new CompositionScene { Source = clip, Volume = double.NaN }] },
                valid with { Scenes = [new CompositionScene { Source = clip, Fit = (FitMode)9 }] },
                valid with { Scenes = [new CompositionScene { Source = clip, Overlays = [new FrameRateTransform(new VideoFrameRate(50))] }] },
                valid with { Scenes = [new CompositionScene { Source = "" }] },
                valid with { Subtitles = [new SubtitleCue(TimeSpan.Zero, TimeSpan.FromSeconds(1), "x")] },
                valid with { EndCard = new CompositionEndCard { Text = "x" } },
                valid with { EndCard = new CompositionEndCard { Text = "x", Duration = TimeSpan.Zero }, Font = TrueTypeFontTest.Noto() },
                valid with { Music = new CompositionMusic { Source = clip, Volume = -1 } },
                valid with { TargetLoudnessLufs = 3 },
                valid with { ConstantRateFactor = 60 },
                valid with { AudioBitRate = 1000 },
                valid with { ThumbnailAt = TimeSpan.FromSeconds(-1) },
                valid with { ThumbnailQuantizerScale = 0 },
                valid with { Scenes = [new CompositionScene { Source = clip, Start = TimeSpan.FromSeconds(10) }] }
            };

            foreach (var composition in invalid)
            {
                using var output = new MemoryStream();
                var act = () => composition.Render(output);
                act.Should().Throw<ArgumentException>();
                output.Length.Should().Be(0);
            }
        }

        [Fact]
        public void Render_Clip_Without_H264_Should_Throw_NotSupportedException()
        {
            var composition = new VideoComposition { Width = 32, Height = 32, Scenes = [new CompositionScene { Source = Mp4FileSourceTest.HevcFixture }] };

            var act = () => composition.Render(new MemoryStream());

            act.Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void Render_Should_Be_Readable_By_Ffmpeg()
        {
            if (Environment.GetEnvironmentVariable("EGGENCODER_FFMPEG_ORACLE") != "1")
            {
                return;
            }

            var output = Path.Combine(_directory, "oracle.mp4");
            new VideoComposition
            {
                Width = 64,
                Height = 64,
                FrameRate = new VideoFrameRate(25),
                Scenes = [new CompositionScene { Source = Clip("tone.mp4", 12, Blue, toneHz: 440) }, new CompositionScene { Source = Mp4FileSourceTest.H264AacFixture }]
            }.Render(output);

            using var process = Process.Start(new ProcessStartInfo("ffprobe", ["-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries", "stream=nb_read_frames,width,height", "-of", "csv=p=0", output]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            var text = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            process.ExitCode.Should().Be(0, process.StandardError.ReadToEnd());
            text.Should().Be("64,64,22");
        }

        private string Clip(string name, int frames, YuvColor color, double toneHz = 0)
        {
            var path = Path.Combine(_directory, name);
            if (!File.Exists(path))
            {
                SyntheticClip.Write(path, 48, 32, frames, 25, color, toneHz);
            }

            return path;
        }

        private static int MeanChroma(VideoFrame frame)
        {
            var sum = 0;
            for (var y = 0; y < frame.ChromaHeight; y++)
            {
                foreach (var value in frame.GetRow(VideoPlane.U, y))
                {
                    sum += value;
                }
            }

            return sum / (frame.ChromaWidth * frame.ChromaHeight);
        }

        private static int MaxLuma(VideoFrame frame) => Enumerable.Range(0, frame.Height).Max(y => frame.GetRow(VideoPlane.Y, y).ToArray().Max());

        private static int MinLuma(VideoFrame frame) => Enumerable.Range(0, frame.Height).Min(y => frame.GetRow(VideoPlane.Y, y).ToArray().Min());

        private static double Rms(PcmAudio audio, double from, double to)
        {
            var first = (int)(from * audio.SampleRate) * audio.Channels;
            var last = Math.Min(audio.Samples.Length, (int)(to * audio.SampleRate) * audio.Channels);
            var sum = 0.0;
            for (var i = first; i < last; i++)
            {
                sum += audio.Samples[i] * audio.Samples[i];
            }

            return Math.Sqrt(sum / Math.Max(1, last - first));
        }

        private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
        {
            public void Report(double value) => report(value);
        }
    }
}
