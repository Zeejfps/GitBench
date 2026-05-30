using ZGF.Gui.HorizontalScrollBar;
using ZGF.Gui.VerticalScrollBar;

namespace GitGui;

/// <summary>
/// Shared helpers for the scroll-sync controllers that mirror a scrollable source view
/// (ScrollPane, DiffContentView, CommitsView) to a stand-alone scroll bar. Collapses the
/// bar's preferred extent to zero when content fits so the parent BorderLayout reclaims
/// the saved space for the center pane.
/// </summary>
internal static class ScrollBarSync
{
    public const float Thickness = 12f;

    public static void ApplyVertical(VerticalScrollBarView bar, float scale, float normalized)
    {
        bar.Width = scale < 1f ? Thickness : 0f;
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
