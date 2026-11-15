namespace EggEncoder.Video;

/// <summary>A rectangle of luma pixels in a frame.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width, 0 or more.</param>
/// <param name="Height">Height, 0 or more.</param>
public readonly record struct VideoRectangle(int X, int Y, int Width, int Height)
{
    /// <summary>The right edge (exclusive).</summary>
    public int Right => X + Width;

    /// <summary>The bottom edge (exclusive).</summary>
    public int Bottom => Y + Height;

    /// <summary>True when the rectangle lies inside a frame of the given size and is not empty.</summary>
    public bool FitsIn(int frameWidth, int frameHeight) =>
        X >= 0 && Y >= 0 && Width > 0 && Height > 0 && (long)X + Width <= frameWidth && (long)Y + Height <= frameHeight;

    /// <summary>The part of the rectangle inside a frame of the given size; empty when they don't overlap.</summary>
    public VideoRectangle ClipTo(int frameWidth, int frameHeight)
    {
        var left = Math.Max(X, 0);
        var top = Math.Max(Y, 0);
        var right = (int)Math.Min((long)X + Width, frameWidth);
        var bottom = (int)Math.Min((long)Y + Height, frameHeight);
        return right <= left || bottom <= top ? default : new VideoRectangle(left, top, right - left, bottom - top);
    }
}
