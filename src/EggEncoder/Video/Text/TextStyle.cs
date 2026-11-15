namespace EggEncoder.Video.Text;

/// <summary>How the lines of a block of text line up with each other.</summary>
public enum TextAlignment
{
    /// <summary>Lines start at the left edge.</summary>
    Left,

    /// <summary>Lines are centred.</summary>
    Center,

    /// <summary>Lines end at the right edge.</summary>
    Right
}

/// <summary>Where rendered text is placed on a frame: one of nine positions, moved inwards by margins.</summary>
public enum TextAnchor
{
    /// <summary>Top left.</summary>
    TopLeft,

    /// <summary>Top, centred horizontally.</summary>
    TopCenter,

    /// <summary>Top right.</summary>
    TopRight,

    /// <summary>Centred vertically, left.</summary>
    MiddleLeft,

    /// <summary>The centre of the frame.</summary>
    Center,

    /// <summary>Centred vertically, right.</summary>
    MiddleRight,

    /// <summary>Bottom left.</summary>
    BottomLeft,

    /// <summary>Bottom, centred horizontally.</summary>
    BottomCenter,

    /// <summary>Bottom right.</summary>
    BottomRight
}

/// <summary>How text looks: size, colour, outline, shadow, background box and line layout. Sizes are in pixels.</summary>
public sealed record TextStyle
{
    /// <summary>Pixels per em (the font size as ffmpeg's <c>drawtext fontsize</c> means it), more than 0 and at most 2048. Default 48.</summary>
    public double FontSize { get; init; } = 48;

    /// <summary>The text colour. Default opaque white.</summary>
    public RgbaColor Color { get; init; } = new(255, 255, 255);

    /// <summary>The outline colour. Default opaque black.</summary>
    public RgbaColor OutlineColor { get; init; } = new(0, 0, 0);

    /// <summary>Outline width in pixels, 0 (none) to 64. Default 0.</summary>
    public double OutlineWidth { get; init; }

    /// <summary>The shadow colour; a shadow is drawn when its alpha is above 0 and the offset is not zero. Default transparent.</summary>
    public RgbaColor ShadowColor { get; init; } = new(0, 0, 0, 0);

    /// <summary>Shadow offset to the right, in pixels, −256 to 256.</summary>
    public int ShadowOffsetX { get; init; }

    /// <summary>Shadow offset downwards, in pixels, −256 to 256.</summary>
    public int ShadowOffsetY { get; init; }

    /// <summary>The colour of a box behind the text; none when its alpha is 0 (the default).</summary>
    public RgbaColor BackgroundColor { get; init; } = new(0, 0, 0, 0);

    /// <summary>Space between the text and the edge of the background box, in pixels, 0 to 1024.</summary>
    public int BackgroundPadding { get; init; }

    /// <summary>Line distance as a multiple of the font's line height (ascender − descender + line gap), 0.5 to 4. Default 1.</summary>
    public double LineSpacing { get; init; } = 1;

    /// <summary>How lines line up. Default <see cref="TextAlignment.Left"/>.</summary>
    public TextAlignment Alignment { get; init; } = TextAlignment.Left;

    /// <summary>Checks every value against its range.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside its range; the exception names the property.</exception>
    public void Validate()
    {
        Require(FontSize > 0 && FontSize <= 2048, nameof(FontSize), FontSize);
        Require(OutlineWidth is >= 0 and <= 64, nameof(OutlineWidth), OutlineWidth);
        Require(ShadowOffsetX is >= -256 and <= 256, nameof(ShadowOffsetX), ShadowOffsetX);
        Require(ShadowOffsetY is >= -256 and <= 256, nameof(ShadowOffsetY), ShadowOffsetY);
        Require(BackgroundPadding is >= 0 and <= 1024, nameof(BackgroundPadding), BackgroundPadding);
        Require(LineSpacing is >= 0.5 and <= 4, nameof(LineSpacing), LineSpacing);
        Require(Enum.IsDefined(Alignment), nameof(Alignment), Alignment);
    }

    internal bool HasShadow => ShadowColor.A > 0 && (ShadowOffsetX != 0 || ShadowOffsetY != 0);

    internal bool HasBackground => BackgroundColor.A > 0;

    internal bool HasOutline => OutlineWidth > 0 && OutlineColor.A > 0;

    private static void Require(bool valid, string name, object value)
    {
        if (!valid)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} is out of range");
        }
    }
}
