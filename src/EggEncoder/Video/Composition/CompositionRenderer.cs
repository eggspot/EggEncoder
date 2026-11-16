using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using EggEncoder.Codecs.H264;
using EggEncoder.Codecs.Jpeg;
using EggEncoder.Codecs.Mov;
using EggEncoder.Containers.Mp4;
using EggEncoder.Media;
using EggEncoder.Pcm;
using EggEncoder.Streaming.Aac;
using EggEncoder.Video.Filters;
using EggEncoder.Video.Text;

namespace EggEncoder.Video.Composition;

internal sealed class CompositionRenderer
{
    private const int AudioSampleRate = 48000;
    private const int AudioChannels = 2;
    private const int VideoTimescale = 90000;
    private const int FrameQueueCapacity = 4;
    private const int EncoderDelayedFrames = H264EncoderSettings.MaxDelayedFramesLimit;
    private const int ThreadsPerConcurrentSegment = 4;

    private readonly VideoComposition _composition;
    private readonly VideoFramePool _pool = new();
    private readonly int _parallelism;

    public CompositionRenderer(VideoComposition composition)
    {
        _composition = composition;
        _parallelism = composition.MaxDegreeOfParallelism > 0 ? composition.MaxDegreeOfParallelism : Environment.ProcessorCount;
    }

    public CompositionResult Render(Stream destination, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var timeline = CompositionTimeline.Plan(_composition);
        cancellationToken.ThrowIfCancellationRequested();
        var audio = CompositionAudio.Build(_composition, timeline, AudioChannels, AudioSampleRate);
        cancellationToken.ThrowIfCancellationRequested();
        var loudness = audio is null ? null : LoudnessMeter.MeasureIntegratedLoudness(audio);
        var segments = CompositionSegments.Plan(_composition, timeline);
        var concurrent = ConcurrentSegmentCount(segments.Count);
        var settings = new H264EncoderSettings
        {
            Width = _composition.Width,
            Height = _composition.Height,
            FrameRate = _composition.FrameRate,
            ConstantRateFactor = _composition.ConstantRateFactor,
            Profile = _composition.Profile,
            ColorRange = VideoColorRange.Limited,
            ColorMatrix = VideoColorMatrix.Bt709,
            MaxDegreeOfParallelism = Math.Max(1, _parallelism / concurrent),
            MaxDelayedFrames = Math.Max(1, EncoderDelayedFrames / concurrent)
        };
        using var encoder = new H264Encoder(settings);
        var videoTrack = new MediaTrackInfo(0, MediaKind.Video, MediaCodec.H264, VideoTimescale, encoder.DecoderConfiguration.ToArray(), 0, width: _composition.Width, height: _composition.Height);
        var tracks = new List<MediaTrackInfo> { videoTrack };
        if (audio is not null)
        {
            tracks.Add(new MediaTrackInfo(1, MediaKind.Audio, MediaCodec.Aac, AudioSampleRate, Mp4EsdsParser.BuildAudioSpecificConfig(AudioChannels, AudioSampleRate), 0, sampleRate: AudioSampleRate, channels: AudioChannels));
        }

        using var scratch = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
        using var writer = new Mp4Writer(destination, leaveOpen: true, scratch);
        writer.Open(new MediaTrackSet(tracks));
        if (audio is not null)
        {
            WriteAudio(writer, audio, cancellationToken);
        }

        var thumbnail = WriteVideo(writer, encoder, settings, timeline, segments, concurrent, progress, cancellationToken);
        writer.Complete();
        progress?.Report(1);
        return new CompositionResult(
            audio is not null,
            _composition.Width,
            _composition.Height,
            _composition.FrameRate.TimestampOf(timeline.TotalFrames),
            (int)timeline.TotalFrames,
            loudness,
            thumbnail);
    }

    private void WriteAudio(Mp4Writer writer, PcmAudio audio, CancellationToken cancellationToken)
    {
        long writtenSamples = 0;
        using var aac = AacStreamingEncoder.Create(AudioChannels, AudioSampleRate, _composition.AudioBitRate, frame =>
        {
            var pts = writtenSamples - AacStreamingEncoder.EncoderDelaySamples;
            writer.Write(new MediaPacket(1, pts, pts, true, 0, frame.Payload.ToArray()));
            writtenSamples += frame.SampleCount;
        });
        var pcm = audio.ToInt16();
        var block = new int[AudioChannels * 4096];
        for (var offset = 0; offset < pcm.Length; offset += block.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(block.Length, pcm.Length - offset);
            for (var i = 0; i < count; i++)
            {
                block[i] = pcm[offset + i];
            }

            aac.Write(block.AsSpan(0, count));
        }

        aac.Complete();
    }

    private int ConcurrentSegmentCount(int segmentCount)
    {
        var wanted = _composition.ConcurrentSegments > 0 ? _composition.ConcurrentSegments : _parallelism / ThreadsPerConcurrentSegment;
        return Math.Clamp(wanted, 1, segmentCount);
    }

    private long ThumbnailFrameIndex(CompositionTimeline timeline)
    {
        for (long index = 0; index < timeline.TotalFrames; index++)
        {
            if (_composition.FrameRate.TimestampOf(index) >= _composition.ThumbnailAt)
            {
                return index;
            }
        }

        return timeline.TotalFrames - 1;
    }

    private byte[] WriteVideo(Mp4Writer writer, H264Encoder firstEncoder, H264EncoderSettings settings, CompositionTimeline timeline, IReadOnlyList<CompositionSegment> segments, int concurrent, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var thumbnailIndex = ThumbnailFrameIndex(timeline);
        var frameParallelism = Math.Max(1, _parallelism / concurrent);
        var configuration = firstEncoder.DecoderConfiguration.ToArray();
        var outputs = new BlockingCollection<MediaPacket>[segments.Count];
        for (var i = 0; i < outputs.Length; i++)
        {
            outputs[i] = new BlockingCollection<MediaPacket>();
        }

        byte[]? thumbnail = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var next = -1;
        var workers = new Task[concurrent];
        for (var w = 0; w < workers.Length; w++)
        {
            workers[w] = Task.Run(() =>
            {
                int index;
                while ((index = Interlocked.Increment(ref next)) < segments.Count)
                {
                    var output = outputs[index];
                    var encoder = index == 0 ? firstEncoder : new H264Encoder(settings);
                    try
                    {
                        if (!encoder.DecoderConfiguration.ToArray().AsSpan().SequenceEqual(configuration))
                        {
                            throw new InvalidOperationException("Every segment's encoder must write the same parameter sets");
                        }

                        var segmentThumbnail = EncodeSegment(segments[index], encoder, timeline, thumbnailIndex, frameParallelism, output.Add, stop.Token);
                        if (segmentThumbnail is not null)
                        {
                            thumbnail = segmentThumbnail;
                        }
                    }
                    catch
                    {
                        stop.Cancel();
                        throw;
                    }
                    finally
                    {
                        output.CompleteAdding();
                        if (index != 0)
                        {
                            encoder.Dispose();
                        }
                    }
                }
            }, CancellationToken.None);
        }

        try
        {
            long written = 0;
            foreach (var output in outputs)
            {
                foreach (var packet in output.GetConsumingEnumerable(stop.Token))
                {
                    writer.Write(packet);
                    written++;
                    progress?.Report(Math.Min(0.999, written / (double)timeline.TotalFrames));
                }
            }

            Task.WaitAll(workers);
            return thumbnail ?? throw new InvalidOperationException("No segment produced the thumbnail frame");
        }
        catch (Exception error)
        {
            stop.Cancel();
            try
            {
                Task.WaitAll(workers);
            }
            catch (AggregateException)
            {
            }

            var failure = workers
                .Where(worker => worker.IsFaulted)
                .SelectMany(worker => worker.Exception!.InnerExceptions)
                .FirstOrDefault(inner => inner is not OperationCanceledException);
            if (failure is not null && error is OperationCanceledException or AggregateException)
            {
                ExceptionDispatchInfo.Throw(failure);
            }

            if (error is AggregateException)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            throw;
        }
        finally
        {
            foreach (var output in outputs)
            {
                output.Dispose();
            }
        }
    }

    private byte[]? EncodeSegment(CompositionSegment segment, H264Encoder encoder, CompositionTimeline timeline, long thumbnailIndex, int frameParallelism, Action<MediaPacket> output, CancellationToken cancellationToken)
    {
        using var frames = new BlockingCollection<VideoFrame>(FrameQueueCapacity);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = Task.Run(() =>
        {
            try
            {
                new CompositionFrameSource(_composition, timeline, _pool, frameParallelism).Produce(segment, frame =>
                {
                    try
                    {
                        frames.Add(frame, stop.Token);
                    }
                    catch
                    {
                        frame.Dispose();
                        throw;
                    }
                }, stop.Token);
            }
            finally
            {
                frames.CompleteAdding();
            }
        }, CancellationToken.None);

        byte[]? thumbnail = null;
        var index = segment.FirstFrame;
        var presentationTimes = new Queue<long>();
        void WritePacket(H264EncodedFrame encoded)
        {
            var pts = presentationTimes.Dequeue();
            output(new MediaPacket(0, pts, pts, encoded.IsKeyframe, 0, encoded.Payload.ToArray()));
        }

        try
        {
            foreach (var frame in frames.GetConsumingEnumerable(cancellationToken))
            {
                using (frame)
                {
                    if (index == thumbnailIndex)
                    {
                        thumbnail = JpegEncoder.Encode(frame, _composition.ThumbnailQuantizerScale);
                    }

                    presentationTimes.Enqueue(MediaTimestamp.Rescale(_composition.FrameRate.TimestampOf(index).Ticks, (int)TimeSpan.TicksPerSecond, VideoTimescale));
                    encoder.Encode(frame, WritePacket);
                    index++;
                }
            }

            producer.GetAwaiter().GetResult();
            encoder.Flush(WritePacket);
            return thumbnail;
        }
        catch
        {
            stop.Cancel();
            foreach (var frame in frames.GetConsumingEnumerable())
            {
                frame.Dispose();
            }

            try
            {
                producer.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }

            throw;
        }
    }
}

internal sealed class CompositionTimeline
{
    private CompositionTimeline(IReadOnlyList<ScenePlan> scenes, long sceneFrames, long totalFrames)
    {
        Scenes = scenes;
        SceneFrames = sceneFrames;
        TotalFrames = totalFrames;
    }

    public IReadOnlyList<ScenePlan> Scenes { get; }

    public long SceneFrames { get; }

    public long TotalFrames { get; }

    public static CompositionTimeline Plan(VideoComposition composition)
    {
        var rate = composition.FrameRate;
        var plans = new List<ScenePlan>();
        var elapsed = TimeSpan.Zero;
        long firstFrame = 0;
        foreach (var scene in composition.Scenes)
        {
            using var reader = Mp4VideoReader.Open(scene.Source);
            var duration = scene.Duration ?? reader.Duration - scene.Start;
            if (duration <= TimeSpan.Zero)
            {
                throw new ArgumentException($"The scene starts at {scene.Start} but '{scene.Source}' is only {reader.Duration} long", nameof(composition));
            }

            elapsed += duration;
            var endFrame = Math.Max(firstFrame + 1, rate.NearestFrameIndex(elapsed));
            plans.Add(new ScenePlan(scene, firstFrame, endFrame, reader.HasAudio));
            firstFrame = endFrame;
        }

        var total = firstFrame;
        if (composition.EndCard is { } card)
        {
            total = Math.Max(total + 1, rate.NearestFrameIndex(rate.TimestampOf(total) + card.Duration));
        }

        return new CompositionTimeline(plans, firstFrame, total);
    }

    internal sealed record ScenePlan(CompositionScene Scene, long FirstFrame, long EndFrame, bool HasAudio)
    {
        public long FrameCount => EndFrame - FirstFrame;
    }
}

internal static class CompositionAudio
{
    public static PcmAudio? Build(VideoComposition composition, CompositionTimeline timeline, int channels, int sampleRate)
    {
        var rate = composition.FrameRate;
        var length = rate.TimestampOf(timeline.TotalFrames);
        var inputs = new List<PcmMixInput>();
        foreach (var plan in timeline.Scenes)
        {
            if (!plan.HasAudio || plan.Scene.Volume == 0)
            {
                continue;
            }

            var clip = PcmAudio.Decode(plan.Scene.Source).ConvertTo(channels, sampleRate);
            var piece = Cut(clip, plan.Scene.Start, rate.TimestampOf(plan.FrameCount));
            inputs.Add(new PcmMixInput(piece, plan.Scene.Volume, rate.TimestampOf(plan.FirstFrame)));
        }

        var hasClipAudio = inputs.Count > 0;
        if (composition.Music is null && !hasClipAudio)
        {
            return null;
        }

        var voice = hasClipAudio
            ? PcmMixer.Mix(inputs, fixedLength: length, channels: channels, sampleRate: sampleRate)
            : PcmAudio.Silence(channels, sampleRate, length);
        var mix = voice;
        if (composition.Music is { } music)
        {
            var source = PcmAudio.Decode(music.Source).ConvertTo(channels, sampleRate);
            var bed = PcmMixer.Mix([new PcmMixInput(source, music.Volume, Loop: music.Loop)], fixedLength: length, channels: channels, sampleRate: sampleRate);
            if (music.Ducking is { } ducking && hasClipAudio)
            {
                bed = SidechainCompressor.Duck(bed, voice, ducking);
            }

            mix = hasClipAudio ? PcmMixer.Mix([new PcmMixInput(voice), new PcmMixInput(bed)], fixedLength: length, channels: channels, sampleRate: sampleRate) : bed;
        }

        return composition.TargetLoudnessLufs is { } target ? LoudnessNormalizer.Normalize(mix, target) : mix;
    }

    private static PcmAudio Cut(PcmAudio audio, TimeSpan start, TimeSpan duration)
    {
        var channels = audio.Channels;
        var first = Math.Min(audio.FrameCount, (long)Math.Round(start.TotalSeconds * audio.SampleRate));
        var count = (long)Math.Round(duration.TotalSeconds * audio.SampleRate);
        var samples = new float[count * channels];
        var available = Math.Min(count, audio.FrameCount - first);
        Array.Copy(audio.Samples, first * channels, samples, 0, available * channels);
        return new PcmAudio(samples, channels, audio.SampleRate);
    }
}

internal sealed class CompositionFrameSource
{
    private readonly VideoComposition _composition;
    private readonly CompositionTimeline _timeline;
    private readonly VideoFramePool _pool;
    private readonly int _parallelism;

    public CompositionFrameSource(VideoComposition composition, CompositionTimeline timeline, VideoFramePool pool, int parallelism)
    {
        _composition = composition;
        _timeline = timeline;
        _pool = pool;
        _parallelism = parallelism;
    }

    public void Produce(CompositionSegment segment, Action<VideoFrame> emit, CancellationToken cancellationToken)
    {
        var renderer = _composition.Font is null ? null : new TextRenderer(_composition.Font);
        var subtitles = _composition.Subtitles.Count > 0 ? new SubtitleBurnInTransform(renderer!, _composition.Subtitles, _composition.SubtitleStyle) : null;
        void Finish(VideoFrame frame)
        {
            if (subtitles is null)
            {
                emit(frame);
            }
            else
            {
                subtitles.Apply(frame, emit);
            }
        }

        foreach (var plan in _timeline.Scenes)
        {
            var first = Math.Max(plan.FirstFrame, segment.FirstFrame);
            var end = Math.Min(plan.EndFrame, segment.EndFrame);
            if (first < end)
            {
                ProduceScene(plan, first, end, Finish, cancellationToken);
            }
        }

        if (_composition.EndCard is { } card)
        {
            var first = Math.Max(_timeline.SceneFrames, segment.FirstFrame);
            var end = Math.Min(_timeline.TotalFrames, segment.EndFrame);
            if (first < end)
            {
                ProduceEndCard(card, renderer!, first, end, Finish, cancellationToken);
            }
        }
    }

    private void ProduceScene(CompositionTimeline.ScenePlan plan, long firstFrame, long endFrame, Action<VideoFrame> emit, CancellationToken cancellationToken)
    {
        var scene = plan.Scene;
        var rate = _composition.FrameRate;
        var prepare = new VideoFrameTransformPipeline(
            new ColorConvertTransform(VideoColorRange.Limited, VideoColorMatrix.Bt709),
            new FitTransform(_composition.Width, _composition.Height, scene.Fit, _composition.Background, ScaleKernel.Bicubic, Math.Max(1, _parallelism / 2)));
        var overlays = new VideoFrameTransformPipeline([.. scene.Overlays]);
        using var reader = Mp4VideoReader.Open(scene.Source, _pool);
        var halfFrame = rate.FrameDuration / 2;
        var next = firstFrame;
        VideoFrame? current = null;
        (VideoFrame Frame, TimeSpan SourceTime)? pending = null;
        try
        {
            using var decoded = reader.ReadFrames(scene.Start + rate.TimestampOf(firstFrame - plan.FirstFrame), dropBefore: scene.Start).GetEnumerator();
            pending = Next(decoded, prepare);
            while (next < endFrame)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var clipTime = scene.Start + rate.TimestampOf(next - plan.FirstFrame);
                while (pending is not null && (current is null || pending.Value.SourceTime <= clipTime + halfFrame))
                {
                    current?.Dispose();
                    current = pending.Value.Frame;
                    pending = Next(decoded, prepare);
                }

                if (current is null)
                {
                    current = _pool.Rent(_composition.Width, _composition.Height, VideoColorRange.Limited, VideoColorMatrix.Bt709);
                    current.Fill(_composition.Background);
                }

                var output = current.Clone();
                output.Timestamp = rate.TimestampOf(next);
                output.Duration = rate.FrameDuration;
                overlays.Apply(output, emit);
                next++;
            }
        }
        finally
        {
            pending?.Frame.Dispose();
            current?.Dispose();
        }
    }

    private static (VideoFrame Frame, TimeSpan SourceTime)? Next(IEnumerator<VideoFrame> decoded, VideoFrameTransformPipeline prepare)
    {
        if (!decoded.MoveNext())
        {
            return null;
        }

        var source = decoded.Current;
        var time = source.Timestamp;
        VideoFrame? fitted = null;
        prepare.Apply(source, frame => fitted = frame);
        return (fitted!, time);
    }

    private void ProduceEndCard(CompositionEndCard card, TextRenderer renderer, long firstFrame, long endFrame, Action<VideoFrame> emit, CancellationToken cancellationToken)
    {
        var rate = _composition.FrameRate;
        using var background = _pool.Rent(_composition.Width, _composition.Height, VideoColorRange.Limited, VideoColorMatrix.Bt709);
        background.Fill(card.Background ?? _composition.Background);
        var text = new TextOverlayTransform(renderer, card.Text, card.Style, TextAnchor.Center, maxWidth: _composition.Width * 9 / 10);
        VideoFrame? drawn = null;
        text.Apply(background.Clone(), frame => drawn = frame);
        using (drawn)
        {
            for (var index = firstFrame; index < endFrame; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var output = drawn!.Clone();
                output.Timestamp = rate.TimestampOf(index);
                output.Duration = rate.FrameDuration;
                emit(output);
            }
        }
    }
}
