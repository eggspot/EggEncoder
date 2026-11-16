using EggEncoder.Media;
using EggEncoder.Video;

namespace EggEncoder.Codecs.H264;

/// <summary>An H.264 encoder: <see cref="H264Encoder"/> on the CPU, or <see cref="Nvenc.NvencH264Encoder"/> on an NVIDIA GPU.</summary>
public interface IH264Encoder : IDisposable
{
    /// <summary>The avcC record for the stream: its SPS and PPS, with 4-byte NAL lengths matching the payloads.</summary>
    AvcDecoderConfigurationRecord DecoderConfiguration { get; }

    /// <summary>Encodes one frame; encoded frames reach <paramref name="output"/> in input order, possibly during a later call.</summary>
    /// <param name="frame">A frame of the configured size; the caller keeps it.</param>
    /// <param name="output">Receives length-prefixed access units.</param>
    void Encode(VideoFrame frame, Action<H264EncodedFrame> output);

    /// <summary>Hands out every frame still held; call once after the last <see cref="Encode"/>.</summary>
    /// <param name="output">Receives the remaining access units.</param>
    void Flush(Action<H264EncodedFrame> output);
}
