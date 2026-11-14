namespace EggEncoder.Video;

/// <summary>The range of 8-bit Y'CbCr values a picture uses.</summary>
public enum VideoColorRange
{
    /// <summary>Studio range: luma 16–235, chroma 16–240 (what H.264 cameras and encoders write by default).</summary>
    Limited,

    /// <summary>Full range: luma and chroma 0–255 (JPEG).</summary>
    Full
}

/// <summary>
/// The matrix that turns R'G'B' into Y'CbCr. A stream that leaves it unspecified is read as
/// <see cref="Bt601"/> below 720 lines and <see cref="Bt709"/> from 720 lines up.
/// </summary>
public enum VideoColorMatrix
{
    /// <summary>ITU-R BT.601 (Kr 0.299, Kb 0.114), standard definition.</summary>
    Bt601,

    /// <summary>ITU-R BT.709 (Kr 0.2126, Kb 0.0722), high definition.</summary>
    Bt709
}

/// <summary>One of the three planes of a <see cref="VideoFrame"/>.</summary>
public enum VideoPlane
{
    /// <summary>Luma, full size.</summary>
    Y,

    /// <summary>Blue-difference chroma (Cb), half size in both directions.</summary>
    U,

    /// <summary>Red-difference chroma (Cr), half size in both directions.</summary>
    V
}
