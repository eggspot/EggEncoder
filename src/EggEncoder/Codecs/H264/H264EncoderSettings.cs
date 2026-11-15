using EggEncoder.Video;

namespace EggEncoder.Codecs.H264;

/// <summary>The H.264 profile an <see cref="H264Encoder"/> writes.</summary>
public enum H264Profile
{
    /// <summary>Constrained Baseline: I and P frames, CAVLC.</summary>
    Baseline,

    /// <summary>Main: what Baseline allows plus CABAC.</summary>
    Main,

    /// <summary>High: what Main allows plus the 8x8 transform.</summary>
    High
}

/// <summary>What an <see cref="H264Encoder"/> produces and how hard it tries.</summary>
public sealed record H264EncoderSettings
{
    /// <summary>Frame width in pixels: even, 2 to 8192.</summary>
    public required int Width { get; init; }

    /// <summary>Frame height in pixels: even, 2 to 8192.</summary>
    public required int Height { get; init; }

    /// <summary>Frames per second, written to the stream's timing information and used for frame durations.</summary>
    public required VideoFrameRate FrameRate { get; init; }

    /// <summary>
    /// Quality on x264's constant rate factor scale, 0 to 51: lower is better and larger, about 6 more
    /// halves the size. 23 by default.
    /// </summary>
    public double ConstantRateFactor { get; init; } = 23;

    /// <summary>Frames between keyframes (IDR pictures); 0 for two seconds' worth. The first frame is always a keyframe.</summary>
    public int KeyframeInterval { get; init; }

    /// <summary>The profile to write; High by default.</summary>
    public H264Profile Profile { get; init; } = H264Profile.High;

    /// <summary>Sample range written to the stream; the first frame's range when <c>null</c>.</summary>
    public VideoColorRange? ColorRange { get; init; }

    /// <summary>YCbCr matrix written to the stream; the first frame's matrix when <c>null</c>.</summary>
    public VideoColorMatrix? ColorMatrix { get; init; }
}

/// <summary>One encoded frame: an access unit of length-prefixed NAL units.</summary>
/// <param name="Payload">The frame's NAL units, each preceded by its 4-byte big-endian length (the AVCC layout MP4, FLV and RTMP use).</param>
/// <param name="IsKeyframe">Whether the frame is an IDR picture a decoder can start from.</param>
/// <param name="Timestamp">The source frame's timestamp.</param>
/// <param name="Duration">One frame at the configured frame rate.</param>
public readonly record struct H264EncodedFrame(ReadOnlyMemory<byte> Payload, bool IsKeyframe, TimeSpan Timestamp, TimeSpan Duration);
