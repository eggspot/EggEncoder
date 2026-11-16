using System.Security.Cryptography;
using EggEncoder.Containers.Mp4;
using EggEncoder.Media;
using EggEncoder.UnitTests.TestUtilities;
using EggEncoder.UnitTests.Video.Text;
using EggEncoder.Video;
using EggEncoder.Video.Composition;
using EggEncoder.Video.Filters;
using EggEncoder.Video.Text;
using FluentAssertions;

namespace EggEncoder.UnitTests.Video.Composition
{
    public sealed class CompositionSegmentsTest : IDisposable
    {
        private static readonly YuvColor Blue = new RgbaColor(30, 60, 200).ToYuv(VideoColorMatrix.Bt709, VideoColorRange.Limited);
        private static readonly YuvColor Green = new RgbaColor(40, 190, 70).ToYuv(VideoColorMatrix.Bt709, VideoColorRange.Limited);

        private readonly string _directory = Directory.CreateTempSubdirectory("eggencoder-segments-").FullName;

        public void Dispose() => Directory.Delete(_directory, recursive: true);

        [Fact]
        public void Plan_Should_Cut_On_Whole_Keyframe_Intervals_And_Keep_Overlay_Scenes_Whole()
        {
            var composition = new VideoComposition
            {
                Width = 48,
                Height = 32,
                FrameRate = new VideoFrameRate(25),
                Scenes =
                [
                    new CompositionScene { Source = Clip("a.mp4", 120, Blue) },
                    new CompositionScene { Source = Clip("b.mp4", 40, Green), Overlays = [new PassThroughOverlay()] },
                    new CompositionScene { Source = Clip("a.mp4", 120, Blue) }
                ],
                ParallelSegmentDuration = TimeSpan.FromSeconds(1)
            };

            var segments = CompositionSegments.Plan(composition, CompositionTimeline.Plan(composition));

            segments.Should().Equal(
                new CompositionSegment(0, 50),
                new CompositionSegment(50, 100),
                new CompositionSegment(100, 160),
                new CompositionSegment(160, 210),
                new CompositionSegment(210, 260),
                new CompositionSegment(260, 280));
        }

        [Fact]
        public void Plan_Should_Fold_A_One_Frame_Tail_Into_The_Last_Segment_And_Keep_One_Segment_When_Null()
        {
            var composition = new VideoComposition
            {
                Width = 48,
                Height = 32,
                FrameRate = new VideoFrameRate(25),
                Scenes = [new CompositionScene { Source = Clip("odd.mp4", 101, Blue) }],
                ParallelSegmentDuration = TimeSpan.FromSeconds(2)
            };
            var timeline = CompositionTimeline.Plan(composition);

            CompositionSegments.Plan(composition, timeline).Should().Equal(new CompositionSegment(0, 50), new CompositionSegment(50, 101));
            CompositionSegments.Plan(composition with { ParallelSegmentDuration = null }, timeline).Should().Equal(new CompositionSegment(0, 101));
        }

        [Fact]
        public void Frames_Produced_In_Any_Split_Should_Equal_The_Sequential_Frames()
        {
            var seekable = Clip("seekable.mp4", 75, Blue, keyframeInterval: 7);
            var composition = new VideoComposition
            {
                Width = 40,
                Height = 40,
                FrameRate = new VideoFrameRate(30),
                Scenes =
                [
                    new CompositionScene { Source = seekable, Start = TimeSpan.FromSeconds(0.36), Duration = TimeSpan.FromSeconds(1.5) },
                    new CompositionScene { Source = seekable, Duration = TimeSpan.FromSeconds(0.6), Fit = FitMode.Contain }
                ],
                Subtitles = [new SubtitleCue(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.7), "Hi")],
                EndCard = new CompositionEndCard { Text = "End", Duration = TimeSpan.FromSeconds(0.3) },
                Font = TrueTypeFontTest.Noto()
            };
            var timeline = CompositionTimeline.Plan(composition);
            var total = timeline.TotalFrames;

            var sequential = Produce(composition, timeline, [new CompositionSegment(0, total)]);
            var split = Produce(composition, timeline, [new CompositionSegment(0, 9), new CompositionSegment(9, 23), new CompositionSegment(23, 50), new CompositionSegment(50, total - 3), new CompositionSegment(total - 3, total)]);

            sequential.Should().HaveCount((int)total);
            split.Should().Equal(sequential);
        }

        [Fact]
        public void Render_In_Concurrent_Segments_Should_Match_A_Single_Segment_Render()
        {
            var composition = new VideoComposition
            {
                Width = 48,
                Height = 32,
                FrameRate = new VideoFrameRate(25),
                Scenes =
                [
                    new CompositionScene { Source = Clip("tone.mp4", 90, Blue, toneHz: 440) },
                    new CompositionScene { Source = Clip("green.mp4", 90, Green) }
                ],
                ThumbnailAt = TimeSpan.FromSeconds(4.5),
                MaxDegreeOfParallelism = 6
            };
            var single = Path.Combine(_directory, "single.mp4");
            var segmented = Path.Combine(_directory, "segmented.mp4");
            var reports = new List<double>();

            var singleResult = (composition with { ParallelSegmentDuration = null }).Render(single);
            var segmentedResult = (composition with { ParallelSegmentDuration = TimeSpan.FromSeconds(1), ConcurrentSegments = 3 }).Render(segmented, new SynchronousProgress(reports.Add));

            segmentedResult.FrameCount.Should().Be(singleResult.FrameCount).And.Be(180);
            segmentedResult.Thumbnail.Should().Equal(singleResult.Thumbnail);
            reports.Should().BeInAscendingOrder();
            reports[^1].Should().Be(1);
            Keyframes(segmented).Should().Equal(Keyframes(single)).And.Equal(0L, 50L, 100L, 150L);
            using var singleReader = Mp4VideoReader.Open(single);
            using var segmentedReader = Mp4VideoReader.Open(segmented);
            var expected = singleReader.ReadFrames().ToList();
            var actual = segmentedReader.ReadFrames().ToList();
            try
            {
                actual.Select(f => f.Timestamp).Should().Equal(expected.Select(f => f.Timestamp));
                for (var i = 0; i < expected.Count; i++)
                {
                    LumaPsnr(actual[i], expected[i]).Should().BeGreaterThan(35, $"frame {i}");
                }
            }
            finally
            {
                expected.ForEach(f => f.Dispose());
                actual.ForEach(f => f.Dispose());
            }
        }

        [Fact]
        public void Render_Should_Throw_A_Segment_Failure_And_Stop_The_Other_Segments()
        {
            var composition = new VideoComposition
            {
                Width = 48,
                Height = 32,
                FrameRate = new VideoFrameRate(25),
                Scenes =
                [
                    new CompositionScene { Source = Clip("a.mp4", 120, Blue) },
                    new CompositionScene { Source = Clip("b.mp4", 40, Green), Overlays = [new ThrowingOverlay()] }
                ],
                ParallelSegmentDuration = TimeSpan.FromSeconds(1),
                ConcurrentSegments = 3
            };

            var act = () => composition.Render(new MemoryStream());

            act.Should().Throw<InvalidOperationException>().WithMessage("overlay failed");
        }

        [Fact]
        public void Render_In_Segments_Cancelled_Midway_Should_Throw_OperationCanceledException()
        {
            var composition = new VideoComposition
            {
                Width = 48,
                Height = 32,
                FrameRate = new VideoFrameRate(25),
                Scenes = [new CompositionScene { Source = Clip("a.mp4", 120, Blue) }, new CompositionScene { Source = Clip("a.mp4", 120, Blue) }],
                ParallelSegmentDuration = TimeSpan.FromSeconds(1),
                ConcurrentSegments = 2
            };
            using var cancellation = new CancellationTokenSource();
            var progress = new SynchronousProgress(value =>
            {
                if (value > 0.2)
                {
                    cancellation.Cancel();
                }
            });

            var act = () => composition.Render(new MemoryStream(), progress, cancellation.Token);

            act.Should().Throw<OperationCanceledException>();
        }

        private static List<string> Produce(VideoComposition composition, CompositionTimeline timeline, CompositionSegment[] segments)
        {
            var hashes = new List<string>();
            var pool = new VideoFramePool();
            foreach (var segment in segments)
            {
                new CompositionFrameSource(composition, timeline, pool, 1).Produce(segment, frame =>
                {
                    using (frame)
                    {
                        hashes.Add($"{frame.Timestamp.Ticks}:{Hash(frame)}");
                    }
                }, CancellationToken.None);
            }

            return hashes;
        }

        private static string Hash(VideoFrame frame)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var y = 0; y < frame.Height; y++)
            {
                hash.AppendData(frame.GetRow(VideoPlane.Y, y));
            }

            for (var y = 0; y < (frame.Height + 1) / 2; y++)
            {
                hash.AppendData(frame.GetRow(VideoPlane.U, y));
                hash.AppendData(frame.GetRow(VideoPlane.V, y));
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }

        private static List<long> Keyframes(string path)
        {
            using var source = Mp4FileSource.Open(path);
            var video = source.Tracks.Tracks.First(t => t.Kind == MediaKind.Video).TrackId;
            var keyframes = new List<long>();
            long index = 0;
            foreach (var packet in source.ReadPackets().Where(p => p.TrackId == video))
            {
                if (packet.IsKeyframe)
                {
                    keyframes.Add(index);
                }

                index++;
            }

            return keyframes;
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

        private string Clip(string name, int frames, YuvColor color, double toneHz = 0, int keyframeInterval = 0)
        {
            var path = Path.Combine(_directory, name);
            if (!File.Exists(path))
            {
                SyntheticClip.Write(path, 48, 32, frames, 25, color, toneHz, keyframeInterval: keyframeInterval);
            }

            return path;
        }

        private sealed class PassThroughOverlay : IVideoFrameTransform
        {
            public bool CanChangeFrameCount => false;

            public (int Width, int Height) GetOutputSize(int inputWidth, int inputHeight) => (inputWidth, inputHeight);

            public void Apply(VideoFrame frame, Action<VideoFrame> emit) => emit(frame);
        }

        private sealed class ThrowingOverlay : IVideoFrameTransform
        {
            public bool CanChangeFrameCount => false;

            public (int Width, int Height) GetOutputSize(int inputWidth, int inputHeight) => (inputWidth, inputHeight);

            public void Apply(VideoFrame frame, Action<VideoFrame> emit)
            {
                frame.Dispose();
                throw new InvalidOperationException("overlay failed");
            }
        }

        private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
        {
            public void Report(double value) => report(value);
        }
    }
}
