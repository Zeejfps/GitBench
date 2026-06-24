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

/// <summary>Corner-radius scale in pixels for chips, badges, and cards.</summary>
public static class Radius
{
    public const int Sm = 4;
    public const int Md = 6;
    public const int Lg = 8;
}
