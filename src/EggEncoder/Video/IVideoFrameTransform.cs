namespace EggEncoder.Video;

/// <summary>
/// One step that edits frames between decode and encode, the video counterpart of <c>Pcm.IPcmTransform</c>.
/// <para>
/// Ownership: <see cref="Apply"/> takes ownership of the frame it is given. It either passes that frame on to
/// <c>emit</c> (changed in place) or disposes it and emits new frames, any number of them (none drops the frame). If it
/// throws, it has disposed the frame first. Frames passed to <c>emit</c> belong to the receiver.
/// </para>
/// </summary>
public interface IVideoFrameTransform
{
    /// <summary>The size of the frames this transform emits for input frames of the given size.</summary>
    (int Width, int Height) GetOutputSize(int inputWidth, int inputHeight);

    /// <summary>True if the transform can emit more or fewer frames than it is given (a frame rate change).</summary>
    bool CanChangeFrameCount { get; }

    /// <summary>Edits <paramref name="frame"/> and passes the result to <paramref name="emit"/>.</summary>
    void Apply(VideoFrame frame, Action<VideoFrame> emit);

    /// <summary>
    /// Emits frames the transform is still holding after the last <see cref="Apply"/> call. Call it once, at the end of
    /// the stream. The default holds nothing.
    /// </summary>
    void Flush(Action<VideoFrame> emit)
    {
    }
}

/// <summary>Frame transforms run in order, each one's output feeding the next, the video counterpart of <c>Pcm.PcmTransformPipeline</c>.</summary>
public sealed class VideoFrameTransformPipeline
{
    private readonly IVideoFrameTransform[] _transforms;

    /// <exception cref="ArgumentException">A transform is null.</exception>
    public VideoFrameTransformPipeline(params IVideoFrameTransform[] transforms)
    {
        _transforms = transforms ?? [];
        if (Array.IndexOf(_transforms, null) >= 0)
        {
            throw new ArgumentException("A transform is null", nameof(transforms));
        }
    }

    /// <summary>The transforms, in the order they run.</summary>
    public IReadOnlyList<IVideoFrameTransform> Transforms => _transforms;

    /// <summary>True when there is at least one transform.</summary>
    public bool HasTransforms => _transforms.Length > 0;

    /// <summary>True when any transform can change the number of frames.</summary>
    public bool CanChangeFrameCount => _transforms.Any(t => t.CanChangeFrameCount);

    /// <summary>The size of the frames the whole pipeline emits for input frames of the given size.</summary>
    public (int Width, int Height) GetOutputSize(int inputWidth, int inputHeight)
    {
        var size = (inputWidth, inputHeight);
        foreach (var transform in _transforms)
        {
            size = transform.GetOutputSize(size.inputWidth, size.inputHeight);
        }

        return size;
    }

    /// <summary>Runs <paramref name="frame"/> through every transform; frames that come out are passed to <paramref name="emit"/>. Takes ownership of <paramref name="frame"/>.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public void Apply(VideoFrame frame, Action<VideoFrame> emit)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(emit);
        Run(0, frame, emit);
    }

    /// <summary>
    /// Flushes every transform in order at the end of the stream: frames one transform flushes run through the
    /// transforms after it before those flush in turn.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="emit"/> is null.</exception>
    public void Flush(Action<VideoFrame> emit)
    {
        ArgumentNullException.ThrowIfNull(emit);
        for (var index = 0; index < _transforms.Length; index++)
        {
            var next = index + 1;
            _transforms[index].Flush(frame => Run(next, frame, emit));
        }
    }

    private void Run(int index, VideoFrame frame, Action<VideoFrame> emit)
    {
        if (index == _transforms.Length)
        {
            emit(frame);
            return;
        }

        var next = index + 1;
        _transforms[index].Apply(frame, output => Run(next, output, emit));
    }
}
