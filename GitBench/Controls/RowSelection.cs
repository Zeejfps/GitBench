using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Geometry;
using ZGF.Gui;

namespace GitBench.Controls;

/// <summary>
/// Paints the shared selected/hovered background for a navigable row list as an inset rounded
/// "pill" with a leading accent bar on the selected row — the look the repo bar established. The
/// branches sidebar and the file lists draw through this so their selection stays identical; the
/// wide commit-history table keeps its own (zero-alloc, column-aware) painter but reads the same
/// <see cref="RowSelectionStyles"/> token.
/// </summary>
internal static class RowSelection
{
    // Leading-edge margin that gives the pill its inset; the trailing edge stays flush with the row.
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
            Style = new RectStyle
            {
                BackgroundColor = bg.Value,
                BorderRadius = BorderRadiusStyle.All(Radius.Sm),
            },
            ZIndex = z,
        });

        if (!isSelected) return;

        // Accent bar down the pill's leading edge, inset vertically so it sits inside the rounded
        // corners rather than poking past them.
        var barLeft = isRtl ? rowRect.Right - PillInset - AccentBarWidth : rowRect.Left + PillInset;
        canvas.DrawRect(new DrawRectInputs
        {
            Position = new RectF(barLeft, rowRect.Bottom + Radius.Sm, AccentBarWidth, rowRect.Height - Radius.Sm * 2),
            Style = new RectStyle { BackgroundColor = styles.AccentBar },
            ZIndex = z + 1,
        });
    }
}
