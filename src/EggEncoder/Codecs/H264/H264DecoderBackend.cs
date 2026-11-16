namespace EggEncoder.Codecs.H264;

/// <summary>Which H.264 decoder reads clips.</summary>
public enum H264DecoderBackend
{
    /// <summary>This library's own decoder on the CPU.</summary>
    Software,

    /// <summary>NVDEC on an NVIDIA GPU; reading fails when <see cref="Nvdec.NvdecH264Decoder.IsSupported"/> is false.</summary>
    Nvdec,

    /// <summary>NVDEC when <see cref="Nvdec.NvdecH264Decoder.IsSupported"/>, otherwise the software decoder.</summary>
    Auto
}
