using EggEncoder.Video;

namespace EggEncoder.Codecs.H264;

/// <summary>An H.264 decoder: <see cref="H264Decoder"/> on the CPU, or <see cref="Nvdec.NvdecH264Decoder"/> on an NVIDIA GPU.</summary>
public interface IH264Decoder : IDisposable
{
    /// <summary>Decodes one access unit; frames reach <paramref name="output"/> in display order, possibly during a later call.</summary>
    /// <param name="accessUnit">NAL units with 4-byte length prefixes.</param>
    /// <param name="timestamp">The access unit's presentation time, carried to its frame.</param>
    /// <param name="output">Receives decoded frames; the receiver owns them.</param>
    void Decode(ReadOnlySpan<byte> accessUnit, TimeSpan timestamp, Action<VideoFrame> output);

    /// <summary>Hands out every frame still held; call at the end of the stream.</summary>
    /// <param name="output">Receives the remaining frames.</param>
    void Flush(Action<VideoFrame> output);
}
