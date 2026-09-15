using ZGF.Gui.Desktop.Components.HorizontalScrollBar;

namespace GitBench.Controls;

/// <summary>
/// Mirrors a scrollable source's extent onto a stand-alone bar. A bar whose content fits is hidden
/// outright, so the layout slot holding it (BorderLayout edge, flex row) collapses and the content
/// reclaims the gutter.
/// </summary>
internal static class ScrollBarSync
{
    public const float Thickness = 12f;

    public static void ApplyVertical(VerticalScrollBar bar, float scale, float normalized)
    {
        bar.IsVisible = scale < 1f;
        bar.Scale = scale;
        bar.SetNormalizedScrollPosition(normalized);
    }

    public static void ApplyHorizontal(HorizontalScrollBarView bar, float scale, float normalized)
    {
        bar.Height = scale < 1f ? Thickness : 0f;
        bar.Scale = scale;
        bar.SetNormalizedScrollPosition(normalized);
    }
}
