using System.Collections.Concurrent;
using EggEncoder.Codecs.H264;
using EggEncoder.Codecs.H264.Nvenc;
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
        var settings = new H264EncoderSettings
        {
            Width = _composition.Width,
            Height = _composition.Height,
            FrameRate = _composition.FrameRate,
            ConstantRateFactor = _composition.ConstantRateFactor,
            Profile = _composition.Profile,
            ColorRange = VideoColorRange.Limited,
            ColorMatrix = VideoColorMatrix.Bt709,
            MaxDegreeOfParallelism = _parallelism,
            MaxDelayedFrames = EncoderDelayedFrames
        };
        using var encoder = CreateEncoder(settings);
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

        var thumbnail = WriteVideo(writer, encoder, timeline, progress, cancellationToken);
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

    private IH264Encoder CreateEncoder(H264EncoderSettings settings) => _composition.VideoEncoder switch
    {
        H264EncoderBackend.Nvenc => new NvencH264Encoder(settings),
        H264EncoderBackend.Auto when settings.Width % 2 == 0 && settings.Height % 2 == 0 && NvencH264Encoder.IsSupported => new NvencH264Encoder(settings),
        _ => new H264Encoder(settings)
    };

    private byte[] WriteVideo(Mp4Writer writer, IH264Encoder encoder, CompositionTimeline timeline, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var frames = new BlockingCollection<VideoFrame>(FrameQueueCapacity);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = Task.Run(() =>
        {
            try
            {
                new CompositionFrameSource(_composition, timeline, _pool, _parallelism).Produce(frame =>
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
        VideoFrame? lastFrame = null;
        long index = 0;
        var presentationTimes = new Queue<long>();
        void WritePacket(H264EncodedFrame encoded)
        {
            var pts = presentationTimes.Dequeue();
            writer.Write(new MediaPacket(0, pts, pts, encoded.IsKeyframe, 0, encoded.Payload));
        }

        try
        {
            foreach (var frame in frames.GetConsumingEnumerable(cancellationToken))
            {
                using (frame)
                {
                    var timestamp = _composition.FrameRate.TimestampOf(index);
                    if (thumbnail is null && timestamp >= _composition.ThumbnailAt)
                    {
                        thumbnail = JpegEncoder.Encode(frame, _composition.ThumbnailQuantizerScale);
                    }

                    presentationTimes.Enqueue(MediaTimestamp.Rescale(timestamp.Ticks, (int)TimeSpan.TicksPerSecond, VideoTimescale));
                    encoder.Encode(frame, WritePacket);
                    if (thumbnail is null)
                    {
                        lastFrame?.Dispose();
                        lastFrame = frame.Clone();
                    }

                    index++;
                    progress?.Report(Math.Min(0.999, index / (double)timeline.TotalFrames));
                }
            }

            producer.GetAwaiter().GetResult();
            encoder.Flush(WritePacket);
            thumbnail ??= JpegEncoder.Encode(lastFrame!, _composition.ThumbnailQuantizerScale);
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
        finally
        {
            lastFrame?.Dispose();
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

    public void Produce(Action<VideoFrame> emit, CancellationToken cancellationToken)
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
            ProduceScene(plan, Finish, cancellationToken);
        }

        if (_composition.EndCard is { } card)
        {
            ProduceEndCard(card, renderer!, Finish, cancellationToken);
        }
    }

    private void ProduceScene(CompositionTimeline.ScenePlan plan, Action<VideoFrame> emit, CancellationToken cancellationToken)
    {
        var scene = plan.Scene;
        var rate = _composition.FrameRate;
        var prepare = new VideoFrameTransformPipeline(
            new ColorConvertTransform(VideoColorRange.Limited, VideoColorMatrix.Bt709),
            new FitTransform(_composition.Width, _composition.Height, scene.Fit, _composition.Background, ScaleKernel.Bicubic, Math.Max(1, _parallelism / 2)));
        var overlays = new VideoFrameTransformPipeline([.. scene.Overlays]);
        using var reader = Mp4VideoReader.Open(scene.Source, _pool, _composition.VideoDecoder);
        var halfFrame = rate.FrameDuration / 2;
        var next = plan.FirstFrame;
        VideoFrame? current = null;
        (VideoFrame Frame, TimeSpan SourceTime)? pending = null;
        try
        {
            using var decoded = reader.ReadFrames(scene.Start).GetEnumerator();
            pending = Next(decoded, prepare);
            while (next < plan.EndFrame)
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

    private void ProduceEndCard(CompositionEndCard card, TextRenderer renderer, Action<VideoFrame> emit, CancellationToken cancellationToken)
    {
        var rate = _composition.FrameRate;
        using var background = _pool.Rent(_composition.Width, _composition.Height, VideoColorRange.Limited, VideoColorMatrix.Bt709);
        background.Fill(card.Background ?? _composition.Background);
        var text = new TextOverlayTransform(renderer, card.Text, card.Style, TextAnchor.Center, maxWidth: _composition.Width * 9 / 10);
        VideoFrame? drawn = null;
        text.Apply(background.Clone(), frame => drawn = frame);
        using (drawn)
        {
            for (var index = _timeline.SceneFrames; index < _timeline.TotalFrames; index++)
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
