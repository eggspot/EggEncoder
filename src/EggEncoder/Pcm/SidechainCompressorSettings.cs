namespace EggEncoder.Pcm;

/// <summary>How <see cref="SidechainCompressor"/> measures the key's level.</summary>
public enum SidechainDetection
{
    /// <summary>The smoothed absolute sample value.</summary>
    Peak,

    /// <summary>The smoothed square of the sample value (a running mean square), ffmpeg's default.</summary>
    Rms
}

/// <summary>How <see cref="SidechainCompressor"/> combines a stereo key's two channels into one level.</summary>
public enum SidechainLink
{
    /// <summary>The average of the two channels' absolute values, ffmpeg's default.</summary>
    Average,

    /// <summary>The larger of the two channels' absolute values.</summary>
    Maximum
}

/// <summary>
/// The settings of a <see cref="SidechainCompressor"/>, named, defaulted and bounded like the options of ffmpeg's
/// <c>sidechaincompress</c> filter (its downward mode). Linear values, not decibels, as in ffmpeg.
/// </summary>
public sealed record SidechainCompressorSettings
{
    /// <summary>Gain applied to the target before compression (ffmpeg <c>level_in</c>), 0.015625 to 64. Default 1.</summary>
    public double InputGain { get; init; } = 1;

    /// <summary>Key level above which the target is reduced, linear, 0.000976563 to 1. Default 0.125.</summary>
    public double Threshold { get; init; } = 0.125;

    /// <summary>How strongly the level above the threshold is reduced, 1 to 20. Default 2.</summary>
    public double Ratio { get; init; } = 2;

    /// <summary>Attack time in milliseconds, 0.01 to 2000. Default 20.</summary>
    public double AttackMs { get; init; } = 20;

    /// <summary>Release time in milliseconds, 0.01 to 9000. Default 250.</summary>
    public double ReleaseMs { get; init; } = 250;

    /// <summary>Gain applied to the compressed target, linear, 1 to 64. Default 1.</summary>
    public double Makeup { get; init; } = 1;

    /// <summary>Width of the soft knee around the threshold as a level ratio, 1 (hard knee) to 8. Default 2.82843.</summary>
    public double Knee { get; init; } = 2.82843;

    /// <summary>How the key's level is measured. Default <see cref="SidechainDetection.Rms"/>.</summary>
    public SidechainDetection Detection { get; init; } = SidechainDetection.Rms;

    /// <summary>How a stereo key's channels are combined. Default <see cref="SidechainLink.Average"/>.</summary>
    public SidechainLink Link { get; init; } = SidechainLink.Average;

    /// <summary>Gain applied to the key before measuring it (ffmpeg <c>level_sc</c>), 0.015625 to 64. Default 1.</summary>
    public double KeyGain { get; init; } = 1;

    /// <summary>Share of the compressed signal in the output, 0 (the target only scaled by <see cref="InputGain"/>) to 1. Default 1.</summary>
    public double Mix { get; init; } = 1;

    /// <summary>Checks every value against its range.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside its range or not a number; the exception names the property.</exception>
    public void Validate()
    {
        Require(InputGain, 0.015625, 64, nameof(InputGain));
        Require(Threshold, 0.000976563, 1, nameof(Threshold));
        Require(Ratio, 1, 20, nameof(Ratio));
        Require(AttackMs, 0.01, 2000, nameof(AttackMs));
        Require(ReleaseMs, 0.01, 9000, nameof(ReleaseMs));
        Require(Makeup, 1, 64, nameof(Makeup));
        Require(Knee, 1, 8, nameof(Knee));
        Require(KeyGain, 0.015625, 64, nameof(KeyGain));
        Require(Mix, 0, 1, nameof(Mix));
        if (!Enum.IsDefined(Detection))
        {
            throw new ArgumentOutOfRangeException(nameof(Detection), Detection, "Unknown detection");
        }

        if (!Enum.IsDefined(Link))
        {
            throw new ArgumentOutOfRangeException(nameof(Link), Link, "Unknown link");
        }
    }

    private static void Require(double value, double min, double max, string name)
    {
        if (double.IsNaN(value) || value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be between {min} and {max}");
        }
    }
}
