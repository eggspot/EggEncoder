namespace EggEncoder.Codecs.H264;

/// <summary>Which H.264 encoder a <see cref="Video.Composition.VideoComposition"/> renders with.</summary>
public enum H264EncoderBackend
{
    /// <summary>This library's own encoder on the CPU; the output doesn't depend on the machine.</summary>
    Software,

    /// <summary>NVENC on an NVIDIA GPU; the render fails when <see cref="Nvenc.NvencH264Encoder.IsSupported"/> is false.</summary>
    Nvenc,

    /// <summary>NVENC when <see cref="Nvenc.NvencH264Encoder.IsSupported"/>, otherwise the software encoder.</summary>
    Auto
}
