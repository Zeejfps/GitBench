using GitBench.Theming;
using ZGF.Geometry;
using ZGF.Gui;

namespace GitBench.Controls;

/// <summary>
/// Paints the shared selected/hovered background for a navigable row list as an inset (leading-edge)
/// fill with an accent bar on the selected row — the look the repo bar established. The branches
/// sidebar and the file lists draw through this so their selection stays identical; the wide
/// commit-history table keeps its own (zero-alloc, column-aware) painter but reads the same
/// <see cref="RowSelectionStyles"/> token.
/// </summary>
internal static class RowSelection
{
    // Leading-edge margin that insets the fill; the trailing edge stays flush with the row.
    public const float PillInset = 6f;
    private const float AccentBarWidth = 2f;

    public static void DrawBackground(
        ICanvas canvas,
        RectF rowRect,
        bool isSelected,
        bool isHovered,
        RowSelectionStyles styles,
        int z,
        bool isRtl = false)
    {
        var bg = isSelected
            ? styles.Fill
            : isHovered ? styles.FillHover : (uint?)null;
        if (bg == null) return;

        var fillLeft = isRtl ? rowRect.Left : rowRect.Left + PillInset;
        canvas.DrawRect(new DrawRectInputs
        {
            Position = new RectF(fillLeft, rowRect.Bottom, rowRect.Width - PillInset, rowRect.Height),
            Style = new RectStyle { BackgroundColor = bg.Value },
            ZIndex = z,
        });

        if (!isSelected) return;

        // Accent bar down the selection's leading edge — the repo bar's active-row marker.
        var barLeft = isRtl ? rowRect.Right - PillInset - AccentBarWidth : rowRect.Left + PillInset;
        canvas.DrawRect(new DrawRectInputs
        {
            Position = new RectF(barLeft, rowRect.Bottom, AccentBarWidth, rowRect.Height),
            Style = new RectStyle { BackgroundColor = styles.AccentBar },
            ZIndex = z + 1,
        });
    }
}
