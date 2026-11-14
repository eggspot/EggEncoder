namespace EggEncoder.Video;

/// <summary>
/// A frame rate as an exact fraction (30000/1001 for NTSC 29.97), so frame times are computed from the frame index
/// rather than accumulated.
/// </summary>
public readonly record struct VideoFrameRate
{
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;

    /// <param name="numerator">Frames, positive.</param>
    /// <param name="denominator">Per this many seconds, positive.</param>
    /// <exception cref="ArgumentOutOfRangeException">A value is not positive.</exception>
    public VideoFrameRate(int numerator, int denominator = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(numerator, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(denominator, 1);
        Numerator = numerator;
        Denominator = denominator;
    }

    /// <summary>Frames per <see cref="Denominator"/> seconds.</summary>
    public int Numerator { get; }

    /// <summary>Seconds per <see cref="Numerator"/> frames.</summary>
    public int Denominator { get; }

    /// <summary>The rate as a floating-point number, for display.</summary>
    public double FramesPerSecond => Numerator / (double)Denominator;

    /// <summary>The time of one frame, rounded to the nearest 100 ns tick.</summary>
    public TimeSpan FrameDuration => TimestampOf(1);

    /// <summary>The time of frame <paramref name="frameIndex"/> counted from 0, rounded to the nearest tick (halves away from zero).</summary>
    public TimeSpan TimestampOf(long frameIndex) =>
        new((long)DivideRounded((Int128)frameIndex * Denominator * TicksPerSecond, Numerator));

    /// <summary>The index of the frame whose time is nearest to <paramref name="time"/> (halves away from zero).</summary>
    public long NearestFrameIndex(TimeSpan time) =>
        (long)DivideRounded((Int128)time.Ticks * Numerator, (Int128)Denominator * TicksPerSecond);

    /// <inheritdoc />
    public override string ToString() => $"{Numerator}/{Denominator}";

    private static Int128 DivideRounded(Int128 dividend, Int128 divisor)
    {
        var half = divisor / 2;
        return dividend >= 0 ? (dividend + half) / divisor : -((-dividend + half) / divisor);
    }
}
