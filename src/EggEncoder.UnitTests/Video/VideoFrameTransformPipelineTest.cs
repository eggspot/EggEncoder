using EggEncoder.Video;
using FluentAssertions;

namespace EggEncoder.UnitTests.Video
{
    public class VideoFrameTransformPipelineTest
    {
        [Fact]
        public void Apply_Should_Run_The_Transforms_In_Order()
        {
            var log = new List<string>();
            var pipeline = new VideoFrameTransformPipeline(new Recording("a", log), new Recording("b", log));
            var output = new List<VideoFrame>();

            pipeline.Apply(VideoFrame.Create(2, 2), output.Add);

            log.Should().Equal("a", "b");
            output.Should().HaveCount(1);
            output[0].Dispose();
        }

        [Fact]
        public void A_Transform_Emitting_Two_Frames_Should_Feed_Both_To_The_Next()
        {
            var log = new List<string>();
            var pipeline = new VideoFrameTransformPipeline(new Doubling(), new Recording("after", log));
            var output = new List<VideoFrame>();

            pipeline.Apply(VideoFrame.Create(2, 2), output.Add);

            log.Should().Equal("after", "after");
            output.Should().HaveCount(2);
            output.ForEach(f => f.Dispose());
            pipeline.CanChangeFrameCount.Should().BeTrue();
        }

        [Fact]
        public void Flush_Should_Pass_A_Flushed_Frame_Through_The_Later_Transforms_Then_Flush_Them()
        {
            var log = new List<string>();
            var pipeline = new VideoFrameTransformPipeline(new Holding(log), new Recording("b", log), new Holding(log, "second"));
            var output = new List<VideoFrame>();

            pipeline.Apply(VideoFrame.Create(2, 2), output.Add);
            pipeline.Flush(output.Add);

            log.Should().Equal("held:first", "flush:first", "b", "held:second", "flush:second");
            output.Should().HaveCount(1);
            output[0].Dispose();
        }

        [Fact]
        public void GetOutputSize_Should_Fold_Over_The_Transforms()
        {
            var pipeline = new VideoFrameTransformPipeline(new Rotating(), new Recording("x", []), new Rotating(), new Rotating());

            pipeline.GetOutputSize(1920, 1080).Should().Be((1080, 1920));
            new VideoFrameTransformPipeline().GetOutputSize(5, 7).Should().Be((5, 7));
        }

        [Fact]
        public void Empty_Pipeline_Should_Pass_Frames_Through()
        {
            var pipeline = new VideoFrameTransformPipeline();
            var frame = VideoFrame.Create(2, 2);
            VideoFrame? received = null;

            pipeline.Apply(frame, f => received = f);
            pipeline.Flush(_ => throw new InvalidOperationException());

            received.Should().BeSameAs(frame);
            pipeline.HasTransforms.Should().BeFalse();
            pipeline.CanChangeFrameCount.Should().BeFalse();
            pipeline.Transforms.Should().BeEmpty();
            frame.Dispose();
        }

        [Fact]
        public void Invalid_Arguments_Should_Throw()
        {
            var pipeline = new VideoFrameTransformPipeline(new Rotating());

            FluentActions.Invoking(() => new VideoFrameTransformPipeline(new Rotating(), null!)).Should().Throw<ArgumentException>();
            pipeline.Invoking(p => p.Apply(null!, _ => { })).Should().Throw<ArgumentNullException>();
            pipeline.Invoking(p => p.Apply(VideoFrame.Create(1, 1), null!)).Should().Throw<ArgumentNullException>();
            pipeline.Invoking(p => p.Flush(null!)).Should().Throw<ArgumentNullException>();
            new VideoFrameTransformPipeline(null!).HasTransforms.Should().BeFalse();
        }

        private sealed class Recording(string name, List<string> log) : IVideoFrameTransform
        {
            public (int Width, int Height) GetOutputSize(int inputWidth, int inputHeight) => (inputWidth, inputHeight);

            public bool CanChangeFrameCount => false;

            public void Apply(VideoFrame frame, Action<VideoFrame> emit)
            {
                log.Add(name);
                emit(frame);
            }
        }

        private sealed class Doubling : IVideoFrameTransform
        {
            public (int Width, int Height) GetOutputSize(int inputWidth, int inputHeight) => (inputWidth, inputHeight);

            public bool CanChangeFrameCount => true;

            public void Apply(VideoFrame frame, Action<VideoFrame> emit)
            {
                emit(frame.Clone());
                emit(frame);
            }
        }

        private sealed class Holding(List<string> log, string name = "first") : IVideoFrameTransform
        {
            private VideoFrame? _held;

            public (int Width, int Height) GetOutputSize(int inputWidth, int inputHeight) => (inputWidth, inputHeight);

            public bool CanChangeFrameCount => true;

            public void Apply(VideoFrame frame, Action<VideoFrame> emit)
            {
                log.Add($"held:{name}");
                _held = frame;
            }

            public void Flush(Action<VideoFrame> emit)
            {
                log.Add($"flush:{name}");
                if (_held is { } held)
                {
                    _held = null;
                    emit(held);
                }
            }
        }

        private sealed class Rotating : IVideoFrameTransform
        {
            public (int Width, int Height) GetOutputSize(int inputWidth, int inputHeight) => (inputHeight, inputWidth);

            public bool CanChangeFrameCount => false;

            public void Apply(VideoFrame frame, Action<VideoFrame> emit) => emit(frame);
        }
    }
}
