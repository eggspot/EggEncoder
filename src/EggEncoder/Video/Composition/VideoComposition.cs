using EggEncoder.Codecs.H264;
using EggEncoder.Codecs.Jpeg;
using EggEncoder.Pcm;
using EggEncoder.Video.Filters;
using EggEncoder.Video.Text;

namespace EggEncoder.Video.Composition;

/// <summary>
/// A short video put together from MP4 clips: each <see cref="CompositionScene"/> is a stretch of one
/// clip fitted to the canvas with its own overlays and volume, the scenes play one after another,
/// then an optional end card; music can loop under it all and duck under the clips' sound;
/// subtitles are burnt in over the whole timeline. <see cref="Render(Stream, IProgress{double}?, CancellationToken)"/>
/// writes it as a progressive MP4 with H.264 video and stereo AAC-LC audio at 48 kHz.
/// </summary>
public sealed record VideoComposition
{
    /// <summary>Canvas width in pixels, 16 to 4096.</summary>
    public required int Width { get; init; }

    /// <summary>Canvas height in pixels, 16 to 4096.</summary>
    public required int Height { get; init; }

    /// <summary>Output frame rate; 30 frames a second by default.</summary>
    public VideoFrameRate FrameRate { get; init; } = new(30);

    /// <summary>The scenes, played in order; at least one.</summary>
    public required IReadOnlyList<CompositionScene> Scenes { get; init; }

    /// <summary>Background music, or none.</summary>
    public CompositionMusic? Music { get; init; }

    /// <summary>A card shown after the last scene, or none.</summary>
    public CompositionEndCard? EndCard { get; init; }

    /// <summary>Subtitle cues on the output timeline; drawn with <see cref="Font"/>.</summary>
    public IReadOnlyList<SubtitleCue> Subtitles { get; init; } = [];

    /// <summary>How subtitles look; libass defaults when null.</summary>
    public SubtitleStyle? SubtitleStyle { get; init; }

    /// <summary>The font for subtitles and the end card; required when either is used.</summary>
    public TrueTypeFont? Font { get; init; }

    /// <summary>
    /// Integrated loudness the final mix is normalised to, in LUFS, with a true-peak ceiling of
    /// <see cref="LoudnessNormalizer.DefaultTruePeakCeilingDb"/>; no normalisation when null. -14 by default.
    /// </summary>
    public double? TargetLoudnessLufs { get; init; } = -14;

    /// <summary>The colour around a contained clip and behind the end card.</summary>
    public YuvColor Background { get; init; } = new(16, 128, 128);

    /// <summary>H.264 quality, as <see cref="H264EncoderSettings.ConstantRateFactor"/>; 23 by default.</summary>
    public double ConstantRateFactor { get; init; } = 23;

    /// <summary>H.264 profile; High by default.</summary>
    public H264Profile Profile { get; init; } = H264Profile.High;

    /// <summary>AAC bit rate in bits per second; 192,000 by default.</summary>
    public int AudioBitRate { get; init; } = 192_000;

    /// <summary>Threads for decoding, filtering and encoding: one per processor when 0 or less.</summary>
    public int MaxDegreeOfParallelism { get; init; }

    /// <summary>Where on the output timeline the thumbnail is taken: the first frame at or after it, or the last frame.</summary>
    public TimeSpan ThumbnailAt { get; init; }

    /// <summary>The thumbnail's JPEG quality on ffmpeg's <c>-q:v</c> scale.</summary>
    public int ThumbnailQuantizerScale { get; init; } = JpegEncoder.DefaultQuantizerScale;

    /// <summary>Renders the composition into <paramref name="destination"/>, which stays open.</summary>
    /// <param name="destination">A writable stream; written from its position, forward only.</param>
    /// <param name="progress">Receives the share of output frames written, from 0 to 1.</param>
    /// <param name="cancellationToken">Stops the render between frames.</param>
    /// <returns>What was written, with a JPEG thumbnail made from the rendered frames.</returns>
    /// <exception cref="ArgumentException">The composition is invalid.</exception>
    /// <exception cref="NotSupportedException">A clip has no H.264 track, or its audio isn't AAC-LC.</exception>
    /// <exception cref="InvalidDataException">A clip or the music file is damaged.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public CompositionResult Render(Stream destination, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        Validate();
        return new CompositionRenderer(this).Render(destination, progress, cancellationToken);
    }

    /// <summary>Renders the composition into a new file at <paramref name="path"/>.</summary>
    /// <inheritdoc cref="Render(Stream, IProgress{double}?, CancellationToken)"/>
    public CompositionResult Render(string path, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        Validate();
        using var file = File.Create(path);
        return new CompositionRenderer(this).Render(file, progress, cancellationToken);
    }

    /// <summary>Renders on a thread-pool thread.</summary>
    /// <inheritdoc cref="Render(Stream, IProgress{double}?, CancellationToken)"/>
    public Task<CompositionResult> RenderAsync(Stream destination, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        Validate();
        return Task.Run(() => new CompositionRenderer(this).Render(destination, progress, cancellationToken), cancellationToken);
    }

    internal void Validate()
    {
        if (Width is < 16 or > 4096 || Height is < 16 or > 4096)
        {
            throw new ArgumentException("The canvas must be 16 to 4096 pixels wide and high", nameof(Width));
        }

        if (Scenes is null || Scenes.Count == 0)
        {
            throw new ArgumentException("A composition needs at least one scene", nameof(Scenes));
        }

        foreach (var scene in Scenes)
        {
            ArgumentNullException.ThrowIfNull(scene, nameof(Scenes));
            scene.Validate();
        }

        Music?.Validate();
        EndCard?.Validate();
        ArgumentNullException.ThrowIfNull(Subtitles, nameof(Subtitles));
        if ((Subtitles.Count > 0 || EndCard is not null) && Font is null)
        {
            throw new ArgumentException("Subtitles and an end card need a font", nameof(Font));
        }

        if (TargetLoudnessLufs is { } target && (double.IsNaN(target) || target is > 0 or < -70))
        {
            throw new ArgumentException("The target loudness must be between -70 and 0 LUFS", nameof(TargetLoudnessLufs));
        }

        if (ConstantRateFactor is < 0 or > 51 || double.IsNaN(ConstantRateFactor))
        {
            throw new ArgumentException("ConstantRateFactor must be between 0 and 51", nameof(ConstantRateFactor));
        }

        if (AudioBitRate is < 32_000 or > 512_000)
        {
            throw new ArgumentException("The audio bit rate must be 32,000 to 512,000 bits per second", nameof(AudioBitRate));
        }

        if (ThumbnailAt < TimeSpan.Zero)
        {
            throw new ArgumentException("The thumbnail position can't be negative", nameof(ThumbnailAt));
        }

        if (ThumbnailQuantizerScale is < 1 or > 31)
        {
            throw new ArgumentException("The thumbnail scale must be 1 to 31", nameof(ThumbnailQuantizerScale));
        }
    }
}

/// <summary>A stretch of one clip on the composition's timeline.</summary>
public sealed record CompositionScene
{
    /// <summary>Path to an MP4/MOV clip with an H.264 track; a name without an extension is fine.</summary>
    public required string Source { get; init; }

    /// <summary>Where in the clip the scene starts.</summary>
    public TimeSpan Start { get; init; }

    /// <summary>How long the scene lasts; to the clip's end when null. The last frame is held when the clip is shorter.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>How the clip fills the canvas; cover (crop) by default.</summary>
    public FitMode Fit { get; init; } = FitMode.Cover;

    /// <summary>
    /// Transforms run on each fitted frame (text, boxes, logos, fades), in order; each must emit one
    /// frame per frame. Frame timestamps are on the output timeline. Each instance must belong to one
    /// scene and one render.
    /// </summary>
    public IReadOnlyList<IVideoFrameTransform> Overlays { get; init; } = [];

    /// <summary>Linear gain on the clip's own sound; 0 mutes it.</summary>
    public double Volume { get; init; } = 1;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrEmpty(Source, nameof(Source));
        if (Start < TimeSpan.Zero)
        {
            throw new ArgumentException("A scene can't start before its clip", nameof(Start));
        }

        if (Duration is { } duration && duration <= TimeSpan.Zero)
        {
            throw new ArgumentException("A scene must last longer than zero", nameof(Duration));
        }

        if (!Enum.IsDefined(Fit))
        {
            throw new ArgumentException("Unknown fit mode", nameof(Fit));
        }

        ArgumentNullException.ThrowIfNull(Overlays, nameof(Overlays));
        if (Overlays.Any(overlay => overlay is null || overlay.CanChangeFrameCount))
        {
            throw new ArgumentException("Overlays must be set and keep one output frame per input frame", nameof(Overlays));
        }

        RequireGain(Volume, nameof(Volume));
    }

    internal static void RequireGain(double gain, string name)
    {
        if (!double.IsFinite(gain) || gain < 0 || gain > 16)
        {
            throw new ArgumentException("A volume must be between 0 and 16", name);
        }
    }
}

/// <summary>Background music under the whole composition.</summary>
public sealed record CompositionMusic
{
    /// <summary>Path to any audio file this library decodes (or an MP4 with AAC); a name without an extension is fine.</summary>
    public required string Source { get; init; }

    /// <summary>Linear gain on the music.</summary>
    public double Volume { get; init; } = 1;

    /// <summary>Start again from the beginning when the music ends; true by default.</summary>
    public bool Loop { get; init; } = true;

    /// <summary>Lowers the music under the clips' sound (ffmpeg <c>sidechaincompress</c>); no ducking when null.</summary>
    public SidechainCompressorSettings? Ducking { get; init; }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrEmpty(Source, nameof(Source));
        CompositionScene.RequireGain(Volume, nameof(Volume));
    }
}

/// <summary>A card of text on a plain background shown after the last scene.</summary>
public sealed record CompositionEndCard
{
    /// <summary>The text; <c>\n</c> breaks lines.</summary>
    public required string Text { get; init; }

    /// <summary>How long the card is shown.</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How the text looks.</summary>
    public TextStyle Style { get; init; } = new();

    /// <summary>The background; the composition's background when null.</summary>
    public YuvColor? Background { get; init; }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Text, nameof(Text));
        ArgumentNullException.ThrowIfNull(Style, nameof(Style));
        if (Duration <= TimeSpan.Zero)
        {
            throw new ArgumentException("The end card must last longer than zero", nameof(Duration));
        }

        Style.Validate();
    }
}

/// <summary>What <see cref="VideoComposition.Render(Stream, IProgress{double}?, CancellationToken)"/> wrote.</summary>
/// <param name="HasAudio">Whether the file has an audio track (some clip had sound, or there was music).</param>
/// <param name="Width">Video width.</param>
/// <param name="Height">Video height.</param>
/// <param name="Duration">Video duration: the number of frames over the frame rate.</param>
/// <param name="FrameCount">Frames written.</param>
/// <param name="IntegratedLoudness">The mix's integrated loudness in LUFS, after normalisation; null without audio or below the gate.</param>
/// <param name="Thumbnail">A JPEG of the frame at <see cref="VideoComposition.ThumbnailAt"/>.</param>
public sealed record CompositionResult(bool HasAudio, int Width, int Height, TimeSpan Duration, int FrameCount, double? IntegratedLoudness, byte[] Thumbnail);
