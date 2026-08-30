using GitBench.Controls;
using GitBench.Features.LocalChanges;
using GitBench.Theming;
using ZGF.Geometry;
using ZGF.Gui;

namespace GitBench.Features.FileBrowser;

/// <summary>
/// Draws one row of the file browser: selection background, ancestry guides, a chevron for a
/// directory, an icon, then the name. Its own painter rather than
/// <see cref="FileChangesUI.DrawFileRow"/>'s because that one is welded to a
/// <see cref="Git.FileChange"/> and a <see cref="Git.DiffSide"/> at every call — there is no file
/// change here, and generalizing it would make the changes panels worse to pay for this. The
/// metrics come from <see cref="TreeMetrics"/>, so the indent rhythm still matches the other trees.
/// </summary>
internal static class FileBrowserRowPainter
{
    public const float RowHeight = 22f;
    public const float RowPaddingLeft = TreeMetrics.BaseIndent;
    public const float RowPaddingRight = 14f;
    public const float ChevronWidth = TreeMetrics.ChevronWidth;
    public const float ChevronGap = 4f;
    public const float IconGap = 6f;
    public const float IndentLevel = TreeMetrics.IndentLevel;

    /// <summary>Where a row's content starts: files reserve the chevron column directories draw
    /// into, so a file's icon lands under its siblings' rather than half a column left of them.</summary>
    public static float ContentLeft(float rowLeft, int depth) =>
        rowLeft + RowPaddingLeft + depth * IndentLevel + ChevronWidth + ChevronGap;

    public static void Draw(
        ICanvas canvas,
        RectF rowRect,
        FileBrowserRow row,
        bool isSelected,
        bool isHovered,
        RowSelectionStyles selection,
        FileBrowserRowStyles colors,
        TextStyle chevronStyle,
        TextStyle iconStyle,
        TextStyle textStyle,
        TextStyle textActiveStyle,
        int z,
        bool isRtl)
    {
        RowSelection.DrawBackground(canvas, rowRect, isSelected, isHovered, selection, z, isRtl: isRtl);
        TreeGuidePainter.Draw(canvas, rowRect, row.Guides, selection.IndentGuide, z + 1, isRtl, gapBridge: 0f);

        var dim = row.IsIgnored || row.IsHidden;
        var isDirectory = row is FileBrowserRow.Directory;
        var left = rowRect.Left + RowPaddingLeft + row.Depth * IndentLevel;

        if (row is FileBrowserRow.Directory directory)
        {
            chevronStyle.TextColor = Tint(colors.DirectoryChevron, dim);
            canvas.DrawText(new DrawTextInputs
            {
                Position = Place(rowRect, left, ChevronWidth, isRtl),
                Text = directory.IsExpanded
                    ? LucideIcons.ChevronDown
                    : isRtl ? LucideIcons.ChevronLeft : LucideIcons.ChevronRight,
                Style = chevronStyle,
                ZIndex = z + 2,
            });
        }

        left += ChevronWidth + ChevronGap;

        var glyph = Glyph(row);
        var iconWidth = canvas.MeasureTextWidth(glyph, iconStyle);
        iconStyle.TextColor = Tint(
            isDirectory ? colors.DirectoryIcon : row.IsLink ? colors.LinkIcon : colors.FileIcon,
            dim);
        canvas.DrawText(new DrawTextInputs
        {
            Position = Place(rowRect, left, iconWidth, isRtl),
            Text = glyph,
            Style = iconStyle,
            ZIndex = z + 2,
        });
        left += iconWidth + IconGap;

        var textWidth = MathF.Max(0f, rowRect.Right - RowPaddingRight - left);
        if (textWidth <= 0f) return;

        var style = isSelected ? textActiveStyle : textStyle;
        style.TextColor = Tint(
            isSelected ? selection.Text : isDirectory ? colors.DirectoryText : colors.FileText,
            dim);
        canvas.DrawText(new DrawTextInputs
        {
            Position = Place(rowRect, left, textWidth, isRtl),
            Text = TextEllipsis.Truncate(canvas, row.Name, style, textWidth),
            Style = style,
            ZIndex = z + 3,
        });
    }

    private static string Glyph(FileBrowserRow row) => row switch
    {
        FileBrowserRow.Directory { IsExpanded: true } => LucideIcons.FolderOpen,
        FileBrowserRow.Directory => LucideIcons.Folder,
        _ when row.IsLink => LucideIcons.FileSymlink,
        _ => LucideIcons.File,
    };

    private static uint Tint(uint color, bool dim) =>
        dim ? (color & 0x00FFFFFFu) | (0x80u << 24) : color;

    private static RectF Place(in RectF rowRect, float left, float width, bool isRtl) =>
        isRtl
            ? new RectF(rowRect.Left + rowRect.Right - left - width, rowRect.Bottom, width, RowHeight)
            : new RectF(left, rowRect.Bottom, width, RowHeight);
}
