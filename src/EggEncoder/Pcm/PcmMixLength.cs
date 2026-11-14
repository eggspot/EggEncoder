namespace EggEncoder.Pcm;

/// <summary>How long a <see cref="PcmMixer"/> mix runs when no fixed length is given (ffmpeg <c>amix duration=</c>).</summary>
public enum PcmMixLength
{
    /// <summary>Until the input that ends last ends.</summary>
    Longest,

    /// <summary>Until the input that ends first ends.</summary>
    Shortest,

    /// <summary>Until the first input ends.</summary>
    First
}
