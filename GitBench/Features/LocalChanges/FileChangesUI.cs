using GitBench.Controls;
using GitBench.Features.Commits;
using GitBench.Localization;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitBench.Features.LocalChanges;

/// <summary>
/// Shared building blocks for the two file-change list flavors — <see cref="FileChangesSection"/>
/// (commit details) and the staged/unstaged panels in <c>LocalChangesView</c>. Both render a
/// titled header bar over a virtualized row list with the same colors, padding, and
/// "Title (count)" format. Visual tweaks made here propagate to all three sites.
/// </summary>
internal static class FileChangesUI
{
    public const int HeaderPadding = 4;
    public const int RowGap = 2;
    public const float BadgeSize = 16f;
    public const float RowHeight = 22f;
    public const float RowPaddingLeft = TreeMetrics.BaseIndent;
    public const float RowPaddingRight = 14f;
    public const float BadgeGap = 8f;

    // Trailing column reserved on every row in a review surface for the per-file "Viewed" check, so
    // the path text width stays put whether or not a row is currently marked viewed.
    public const float ViewedColumnWidth = 16f;

    public static string FormatHeader(string title, int count) => $"{title} ({count})";

    public static TextView CreateHeaderText(Context ctx, string title)
    {
        var view = new TextView(ctx.Canvas) { Text = FormatHeader(title, 0) };
        view.BindThemedTextColor(ctx.Theme(), s => s.FileChangesSection.HeaderText);
        return view;
    }

    public static TextView CreateEmptyPlaceholder(Context ctx, string emptyText)
    {
        var view = new TextView(ctx.Canvas) { Text = emptyText };
        view.BindThemedTextColor(ctx.Theme(), s => s.FileChangesSection.EmptyPlaceholderText);
        return view;
    }

    /// <summary>
    /// Centered icon / title / hint stack shown when a panel has no rows. Title and hint are
    /// bound to the active locale (selectors off <see cref="Strings"/>) so they re-localize on a
    /// live language switch rather than freezing at construction.
    /// </summary>
    public static View CreateEmptyState(
        Context ctx,
        string icon,
        IReadable<Strings> strings,
        Func<Strings, string> title,
        Func<Strings, string> hint)
    {
        var theme = ctx.Theme();
        var iconView = new TextView(ctx.Canvas)
        {
            Text = icon,
            FontFamily = LucideIcons.FontFamily,
            FontSize = FontSize.Display,
            HorizontalTextAlignment = TextAlignment.Center,
        };
        iconView.BindThemedTextColor(theme, s => s.FileChangesSection.EmptyStateIcon);

        var titleView = new TextView(ctx.Canvas)
        {
            HorizontalTextAlignment = TextAlignment.Center,
        };
        titleView.Bind(strings, s => titleView.Text = title(s));
        titleView.BindThemedTextColor(theme, s => s.FileChangesSection.EmptyPlaceholderText);

        var hintView = new TextView(ctx.Canvas)
        {
            FontSize = FontSize.Caption,
            HorizontalTextAlignment = TextAlignment.Center,
            TextWrap = TextWrap.Wrap,
        };
        hintView.Bind(strings, s => hintView.Text = hint(s));
        hintView.BindThemedTextColor(theme, s => s.FileChangesSection.EmptyStateHint);

        return new PaddingView
        {
            Padding = new PaddingStyle { Left = Spacing.Xl, Right = Spacing.Xl },
            Children =
            {
                new FlexColumnView
                {
                    MainAxisAlignment = MainAxisAlignment.Center,
                    CrossAxisAlignment = CrossAxisAlignment.Stretch,
                    Gap = Spacing.Md,
                    Children = { iconView, titleView, hintView },
                },
            },
        };
    }

    public static RectView CreateHeaderBar(Context ctx, View content)
    {
        var theme = ctx.Theme();
        var view = new RectView
        {
            BorderSize = new BorderSizeStyle { Top = 1, Bottom = 1 },
            Children =
            {
                new PaddingView
                {
                    Padding = new PaddingStyle
                    {
                        Left = HeaderPadding,
                        Right = HeaderPadding,
                        Top = HeaderPadding,
                        Bottom = HeaderPadding,
                    },
                    Children = { content },
                },
            },
        };
        view.BindThemedBackgroundColor(theme, s => s.FileChangesSection.HeaderBackground);
        view.BindThemedBorderColor(theme, s => new BorderColorStyle
        {
            Top = s.FileChangesSection.HeaderBorder,
            Bottom = s.FileChangesSection.HeaderBorder,
        });
        return view;
    }

    /// <summary>
    /// Draws one row of a file-change list at <paramref name="rowRect"/>: selection/hover
    /// background, a status-representing Lucide glyph tinted by the file's status color, then
    /// the truncated file path. The tinted line icon mirrors the folder/branch icons in the
    /// other tree views. Used by both <see cref="FileChangesSection"/> and
    /// <c>LocalChangesPanel</c> so the two flavors stay visually identical.
    /// </summary>
    public static void DrawFileRow(
        ICanvas canvas,
        RectF rowRect,
        FileChange file,
        bool isSelected,
        bool isHovered,
        RowSelectionStyles selection,
        FileChangeRowStyles styles,
        TextStyle pathStyle,
        TextStyle pathActiveStyle,
        TextStyle statusIconStyle,
        int z,
        string? displayText = null,
        float indent = 0f,
        bool reserveChevronColumn = false,
        bool isRtl = false,
        bool drawSelectionBackground = true,
        bool reserveViewedColumn = false,
        bool isViewed = false,
        TextStyle? viewedIconStyle = null,
        bool drawSelectionAccent = true,
        TreeGuides guides = default)
    {
        // When the host floats an animated selection bar it owns the selected row's fill; skip the
        // static one so the two don't double-paint, but still draw hover on non-selected rows.
        if (drawSelectionBackground || !isSelected)
            RowSelection.DrawBackground(
                canvas, rowRect, isSelected, isHovered, selection, z,
                isRtl: isRtl, drawAccentBar: drawSelectionAccent);

        // Rows in these lists sit flush against each other (no inter-row gap), so no bridging overrun.
        TreeGuidePainter.Draw(canvas, rowRect, guides, selection.IndentGuide, z + 1, isRtl, gapBridge: 0f);

        // In tree mode, reserve the same chevron column folder rows draw into so a file's
        // icon lines up under sibling folder icons (and one level right of its parent's).
        var iconLeft = rowRect.Left + RowPaddingLeft + indent
            + (reserveChevronColumn ? ChevronWidth + ChevronGap : 0f);
        statusIconStyle.TextColor = styles.StatusColor(file.Status);
        canvas.DrawText(new DrawTextInputs
        {
            Position = Place(rowRect, iconLeft, BadgeSize, isRtl),
            Text = FileChangeFormatting.StatusIcon(file.Status),
            Style = statusIconStyle,
            ZIndex = z + 1,
        });

        var textLeft = iconLeft + BadgeSize + BadgeGap;
        // Keep clear of the trailing Viewed column (reserved whether or not this row is viewed) so the
        // check never overlaps a long path and the text doesn't reflow when a row is ticked.
        var rightInset = RowPaddingRight + (reserveViewedColumn ? ViewedColumnWidth : 0f);
        var textRight = rowRect.Right - rightInset;
        var textWidth = Math.Max(0f, textRight - textLeft);
        if (textWidth <= 0f) return;

        var renderStyle = isSelected ? pathActiveStyle : pathStyle;
        var pathText = displayText ?? FileChangeFormatting.FormatPath(file);
        var rendered = TextMeasure.TruncateToFit(pathText, renderStyle, textWidth, canvas);
        // A viewed file is "done" — dim its label (half alpha) so the eye skips to what's left.
        var baseColor = renderStyle.TextColor;
        if (isViewed) renderStyle.TextColor = Dim(baseColor);
        canvas.DrawText(new DrawTextInputs
        {
            Position = Place(rowRect, textLeft, textWidth, isRtl),
            Text = rendered,
            Style = renderStyle,
            ZIndex = z + 2,
        });
        renderStyle.TextColor = baseColor;

        if (isViewed && viewedIconStyle != null)
        {
            var checkLeft = rowRect.Right - RowPaddingRight - ViewedColumnWidth;
            canvas.DrawText(new DrawTextInputs
            {
                Position = Place(rowRect, checkLeft, ViewedColumnWidth, isRtl),
                Text = LucideIcons.CheckSquare,
                Style = viewedIconStyle,
                ZIndex = z + 2,
            });
        }
    }

    // Halves a packed 0xAARRGGBB color's alpha, leaving RGB intact — the "viewed/done" dim.
    private static uint Dim(uint color) => (color & 0x00FFFFFFu) | (0x80u << 24);

    // Reflects an element's horizontal extent within the row when the UI is right-to-left, so these
    // shared left-origin row painters mirror (status/chevron/icon to the right, path text flowing
    // left) without rewriting their layout. In-box text right-aligns via the canvas text base.
    private static RectF Place(in RectF rowRect, float left, float width, bool isRtl) =>
        isRtl
            ? new RectF(rowRect.Left + rowRect.Right - left - width, rowRect.Bottom, width, RowHeight)
            : new RectF(left, rowRect.Bottom, width, RowHeight);

    public const float ChevronWidth = 12f;
    public const float ChevronGap = 4f;
    public const float FolderIconGap = 6f;

    /// <summary>
    /// Draws a tree-mode directory row: collapse chevron, folder icon, then the (possibly
    /// compacted) folder name. <paramref name="isSelected"/> highlights when the folder
    /// row itself is selected.
    /// </summary>
    public static void DrawFolderRow(
        ICanvas canvas,
        RectF rowRect,
        string displayName,
        float indent,
        bool isOpen,
        bool isSelected,
        bool isHovered,
        RowSelectionStyles selection,
        TextStyle chevronStyle,
        TextStyle folderIconStyle,
        TextStyle textStyle,
        TextStyle textActiveStyle,
        int z,
        bool isRtl = false,
        bool drawSelectionBackground = true,
        TreeGuides guides = default)
    {
        if (drawSelectionBackground || !isSelected)
            RowSelection.DrawBackground(canvas, rowRect, isSelected, isHovered, selection, z, isRtl: isRtl);

        TreeGuidePainter.Draw(canvas, rowRect, guides, selection.IndentGuide, z + 1, isRtl, gapBridge: 0f);

        var left = rowRect.Left + RowPaddingLeft + indent;

        canvas.DrawText(new DrawTextInputs
        {
            Position = Place(rowRect, left, ChevronWidth, isRtl),
            Text = isOpen
                ? LucideIcons.ChevronDown
                : isRtl ? LucideIcons.ChevronLeft : LucideIcons.ChevronRight,
            Style = chevronStyle,
            ZIndex = z + 1,
        });
        left += ChevronWidth + ChevronGap;

        var folderGlyph = isOpen ? LucideIcons.FolderOpen : LucideIcons.Folder;
        var iconWidth = canvas.MeasureTextWidth(folderGlyph, folderIconStyle);
        canvas.DrawText(new DrawTextInputs
        {
            Position = Place(rowRect, left, iconWidth, isRtl),
            Text = folderGlyph,
            Style = folderIconStyle,
            ZIndex = z + 1,
        });
        left += iconWidth + FolderIconGap;

        var textRight = rowRect.Right - RowPaddingRight;
        var textWidth = Math.Max(0f, textRight - left);
        if (textWidth <= 0f) return;

        var style = isSelected ? textActiveStyle : textStyle;
        var rendered = TextMeasure.TruncateToFit(displayName, style, textWidth, canvas);
        canvas.DrawText(new DrawTextInputs
        {
            Position = Place(rowRect, left, textWidth, isRtl),
            Text = rendered,
            Style = style,
            ZIndex = z + 1,
        });
    }
}
