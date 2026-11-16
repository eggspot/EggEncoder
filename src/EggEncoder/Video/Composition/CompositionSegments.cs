using EggEncoder.Codecs.H264;

namespace EggEncoder.Video.Composition;

internal readonly record struct CompositionSegment(long FirstFrame, long EndFrame);

internal static class CompositionSegments
{
    private const int MinSegmentFrames = 2;

    public static IReadOnlyList<CompositionSegment> Plan(VideoComposition composition, CompositionTimeline timeline)
    {
        var total = timeline.TotalFrames;
        if (composition.ParallelSegmentDuration is not { } duration)
        {
            return [new CompositionSegment(0, total)];
        }

        var keyframeInterval = H264EncoderSettings.DefaultKeyframeInterval(composition.FrameRate);
        var wholeIntervals = Math.Max(1, (composition.FrameRate.NearestFrameIndex(duration) + keyframeInterval - 1) / keyframeInterval);
        var segmentFrames = Math.Max(MinSegmentFrames, wholeIntervals * keyframeInterval);
        var segments = new List<CompositionSegment>();
        long start = 0;
        while (true)
        {
            var cut = MoveOutOfUnsplittableScenes(timeline, start + segmentFrames);
            if (total - cut < MinSegmentFrames)
            {
                break;
            }

            segments.Add(new CompositionSegment(start, cut));
            start = cut;
        }

        segments.Add(new CompositionSegment(start, total));
        return segments;
    }

    private static long MoveOutOfUnsplittableScenes(CompositionTimeline timeline, long cut)
    {
        foreach (var plan in timeline.Scenes)
        {
            if (plan.Scene.Overlays.Count > 0 && plan.FirstFrame < cut && cut < plan.EndFrame)
            {
                return plan.EndFrame;
            }
        }

        return cut;
    }
}
