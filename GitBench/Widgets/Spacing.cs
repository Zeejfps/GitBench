namespace GitBench.Widgets;

/// <summary>
/// Layout spacing scale in pixels — one ladder of named steps for gaps and paddings so spacing
/// reads consistently instead of scattering magic numbers. Plain ints (spacing is identical in
/// light and dark); they implicitly widen to the float Gap used by widget rows/columns.
/// </summary>
public static class Spacing
{
    public const int None = 0;
    public const int Hair = 2;
    public const int Xs = 4;
    public const int Sm = 6;
    public const int Md = 8;
    public const int Lg = 12;
    public const int Xl = 16;
    public const int Xxl = 24;
}

/// <summary>
/// Pointer-scroll tuning shared by every scrollable surface — the repo bar, branches, commit
/// history, file lists, diffs, and dialogs all read this so wheel speed stays uniform instead of
/// each scroll view picking its own. Theme-independent, like the spacing scale.
/// </summary>
public static class Scrolling
{
    /// <summary>Pixels travelled per mouse-wheel notch.</summary>
    public const float WheelStep = 60f;
}

/// <summary>Corner-radius scale in pixels for chips, badges, and cards.</summary>
public static class Radius
{
    public const int Sm = 4;
    public const int Md = 6;
    public const int Lg = 8;
}

/// <summary>
/// Type scale in pixels — one ladder of named steps so text size reads consistently instead of
/// scattering magic numbers. Plain ints (type sizes don't vary by theme); they implicitly widen
/// to the float FontSize the text styles expect.
/// </summary>
public static class FontSize
{
    public const int Caption = 11;
    public const int Body = 13;
    public const int Default = 14;
    public const int Heading = 16;
    public const int Title = 22;
    public const int Display = 28;
    public const int Hero = 60;
}

/// <summary>
/// Standard control and icon dimensions in pixels — the recurring layout sizes shared across
/// widgets so they stay uniform instead of being retyped as magic numbers. Bespoke one-off
/// dimensions (dialog widths, component-specific heights) stay as plain literals.
/// </summary>
public static class Sizes
{
    /// <summary>Compact row / checkbox height.</summary>
    public const int RowHeight = 22;

    /// <summary>Standard button, input, and primary-row height.</summary>
    public const int ControlHeight = 28;

    /// <summary>Standard icon glyph box.</summary>
    public const int Icon = 16;
}
