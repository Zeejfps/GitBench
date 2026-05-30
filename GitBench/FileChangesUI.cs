using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;

namespace GitGui;

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

    public static string FormatHeader(string title, int count) => $"{title} ({count})";

    public static TextView CreateHeaderText(string title)
    {
        var view = new TextView { Text = FormatHeader(title, 0) };
        view.BindThemedTextColor(s => s.FileChangesSection.HeaderText);
        return view;
    }

    public static TextView CreateEmptyPlaceholder(string emptyText)
    {
        var view = new TextView { Text = emptyText };
        view.BindThemedTextColor(s => s.FileChangesSection.EmptyPlaceholderText);
        return view;
    }

    public static RectView CreateHeaderBar(View content)
    {
        var view = new RectView
        {
            BorderSize = new BorderSizeStyle { Top = 1, Bottom = 1 },
            Padding = new PaddingStyle
            {
                Left = HeaderPadding,
                Right = HeaderPadding,
                Top = HeaderPadding,
                Bottom = HeaderPadding,
            },
            Children = { content },
        };
        view.BindThemedBackgroundColor(s => s.FileChangesSection.HeaderBackground);
        view.BindThemedBorderColor(s => new BorderColorStyle
        {
            Top = s.FileChangesSection.HeaderBorder,
            Bottom = s.FileChangesSection.HeaderBorder,
        });
        return view;
    }

    /// <summary>Square colored badge containing the single-letter status glyph for a file.</summary>
    public static RectView CreateStatusBadge(FileChange file)
    {
        var status = file.Status;
        var glyph = new TextView
        {
            Text = FileChangeFormatting.StatusGlyph(status),
            FontSize = 11f,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        glyph.BindThemedTextColor(s => s.FileChangeRow.BadgeText);

        var badge = new RectView
        {
            Width = BadgeSize,
            Height = BadgeSize,
            BorderRadius = BorderRadiusStyle.All(3),
            Children = { glyph },
        };
        badge.BindThemedBackgroundColor(s => s.FileChangeRow.StatusColor(status));
        return badge;
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
        FileChangeRowStyles styles,
        TextStyle pathStyle,
        TextStyle pathActiveStyle,
        TextStyle statusIconStyle,
        int z,
        string? displayText = null,
        float indent = 0f,
        bool reserveChevronColumn = false)
    {
        var bg = isSelected
            ? styles.RowActive
            : (isHovered ? styles.RowHover : (uint?)null);
        if (bg != null)
        {
            canvas.DrawRect(new DrawRectInputs
            {
                Position = rowRect,
                Style = new RectStyle
                {
                    BackgroundColor = bg.Value,
                    BorderRadius = BorderRadiusStyle.All(3),
                },
                ZIndex = z,
            });
        }

        // In tree mode, reserve the same chevron column folder rows draw into so a file's
        // icon lines up under sibling folder icons (and one level right of its parent's).
        var iconLeft = rowRect.Left + RowPaddingLeft + indent
            + (reserveChevronColumn ? ChevronWidth + ChevronGap : 0f);
        statusIconStyle.TextColor = styles.StatusColor(file.Status);
        canvas.DrawText(new DrawTextInputs
        {
            Position = new RectF(iconLeft, rowRect.Bottom, BadgeSize, RowHeight),
            Text = FileChangeFormatting.StatusIcon(file.Status),
            Style = statusIconStyle,
            ZIndex = z + 1,
        });

        var textLeft = iconLeft + BadgeSize + BadgeGap;
        var textRight = rowRect.Right - RowPaddingRight;
        var textWidth = Math.Max(0f, textRight - textLeft);
        if (textWidth <= 0f) return;

        var renderStyle = isSelected ? pathActiveStyle : pathStyle;
        var pathText = displayText ?? FileChangeFormatting.FormatPath(file);
        var rendered = TextMeasure.TruncateToFit(pathText, renderStyle, textWidth, canvas);
        canvas.DrawText(new DrawTextInputs
        {
            Position = new RectF(textLeft, rowRect.Bottom, textWidth, RowHeight),
            Text = rendered,
            Style = renderStyle,
            ZIndex = z + 2,
        });
    }

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
        FileChangeRowStyles styles,
        TextStyle chevronStyle,
        TextStyle folderIconStyle,
        TextStyle textStyle,
        TextStyle textActiveStyle,
        int z)
    {
        var bg = isSelected
            ? styles.RowActive
            : (isHovered ? styles.RowHover : (uint?)null);
        if (bg != null)
        {
            canvas.DrawRect(new DrawRectInputs
            {
                Position = rowRect,
                Style = new RectStyle { BackgroundColor = bg.Value, BorderRadius = BorderRadiusStyle.All(3) },
                ZIndex = z,
            });
        }

        var left = rowRect.Left + RowPaddingLeft + indent;

        canvas.DrawText(new DrawTextInputs
        {
            Position = new RectF(left, rowRect.Bottom, ChevronWidth, RowHeight),
            Text = isOpen ? LucideIcons.ChevronDown : LucideIcons.ChevronRight,
            Style = chevronStyle,
            ZIndex = z + 1,
        });
        left += ChevronWidth + ChevronGap;

        var folderGlyph = isOpen ? LucideIcons.FolderOpen : LucideIcons.Folder;
        var iconWidth = canvas.MeasureTextWidth(folderGlyph, folderIconStyle);
        canvas.DrawText(new DrawTextInputs
        {
            Position = new RectF(left, rowRect.Bottom, iconWidth, RowHeight),
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
            Position = new RectF(left, rowRect.Bottom, textWidth, RowHeight),
            Text = rendered,
            Style = style,
            ZIndex = z + 1,
        });
    }
}
