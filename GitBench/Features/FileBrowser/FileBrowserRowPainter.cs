using GitBench.Widgets;
using GitBench.Controls;
using GitBench.Features.CodeIntel;
using GitBench.Features.LocalChanges;
using GitBench.Theming;
using ZGF.Geometry;
using ZGF.Gui;

namespace GitBench.Features.FileBrowser;

/// <summary>
/// Draws one row of the file browser: selection background, ancestry guides, a chevron for anything
/// that opens, an icon, then the name. Its own painter rather than
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

        if (Chevron(row) is { } open)
        {
            chevronStyle.TextColor = Tint(colors.DirectoryChevron, dim);
            canvas.DrawText(new DrawTextInputs
            {
                Position = Place(rowRect, left, ChevronWidth, isRtl),
                Text = open
                    ? LucideIcons.ChevronDown
                    : isRtl ? LucideIcons.ChevronLeft : LucideIcons.ChevronRight,
                Style = chevronStyle,
                ZIndex = z + 2,
            });
        }

        left += ChevronWidth + ChevronGap;

        // The style is shared across rows, so family, size and colour are all set every row rather
        // than only when they change — a row that inherited the previous one's font would look up
        // its glyph in the wrong one, and a size derived from the current value would compound.
        var (glyph, family) = Glyph(row);
        iconStyle.FontFamily = family;
        iconStyle.FontSize = family == SetiIcons.FontFamily ? SetiIconSize : IconSize;
        iconStyle.HorizontalAlignment = TextAlignment.Center;
        iconStyle.TextColor = Tint(
            isDirectory ? colors.DirectoryIcon
            : row is FileBrowserRow.Symbol ? colors.FileIcon
            : row.IsLink ? colors.LinkIcon
            : colors.IconFor(FileKinds.Classify(row.Name)),
            dim);
        canvas.DrawText(new DrawTextInputs
        {
            Position = Place(rowRect, left, IconColumnWidth, isRtl),
            Text = glyph,
            Style = iconStyle,
            ZIndex = z + 2,
        });
        left += IconColumnWidth + IconGap;

        if (row is FileBrowserRow.Symbol symbol)
        {
            DrawSymbol(canvas, rowRect, symbol, left, isSelected, selection, colors, textStyle, textActiveStyle, z, isRtl);
            return;
        }

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

    /// <summary>A declaration's name, then its parameter list dimmed behind it, so the name is what
    /// the eye lands on and the overload is still legible.</summary>
    private static void DrawSymbol(
        ICanvas canvas,
        RectF rowRect,
        FileBrowserRow.Symbol symbol,
        float left,
        bool isSelected,
        RowSelectionStyles selection,
        FileBrowserRowStyles colors,
        TextStyle textStyle,
        TextStyle textActiveStyle,
        int z,
        bool isRtl)
    {
        var dim = symbol.IsIgnored || symbol.IsHidden;
        var available = MathF.Max(0f, rowRect.Right - RowPaddingRight - left);
        if (available <= 0f) return;

        var style = isSelected ? textActiveStyle : textStyle;
        style.TextColor = Tint(isSelected ? selection.Text : colors.FileText, dim);
        var name = TextEllipsis.Truncate(canvas, symbol.Name, style, available);
        var nameWidth = canvas.MeasureTextWidth(name, style);
        canvas.DrawText(new DrawTextInputs
        {
            Position = Place(rowRect, left, nameWidth, isRtl),
            Text = name,
            Style = style,
            ZIndex = z + 3,
        });

        if (symbol.ParameterTypes is null) return;

        var rest = available - nameWidth;
        if (rest <= 0f) return;

        var parameterStyle = style;
        parameterStyle.TextColor = Tint(style.TextColor, dim: true);
        canvas.DrawText(new DrawTextInputs
        {
            Position = Place(rowRect, left + nameWidth, rest, isRtl),
            Text = TextEllipsis.Truncate(canvas, $"({symbol.ParameterTypes})", parameterStyle, rest),
            Style = parameterStyle,
            ZIndex = z + 3,
        });
    }

    private static bool? Chevron(FileBrowserRow row) => row switch
    {
        FileBrowserRow.Directory directory => directory.IsExpanded,
        FileBrowserRow.File { IsExpandable: true } file => file.IsExpanded,
        FileBrowserRow.Symbol { IsExpandable: true } container => container.IsExpanded,
        _ => null,
    };

    /// <summary>
    /// The mark a row draws, and the font it lives in. A file the parser has a grammar for gets its
    /// language's Seti mark, so the icon set and the outline set say the same thing: a row wearing a
    /// language mark is a row whose declarations the tree can open.
    /// </summary>
    /// <remarks>
    /// A symlink keeps the link mark whatever it points at — that it is a link is the more
    /// surprising fact about it, and the one a reader is scanning for.
    /// </remarks>
    private static (string Glyph, string Family) Glyph(FileBrowserRow row) => row switch
    {
        FileBrowserRow.Directory { IsExpanded: true } => (LucideIcons.FolderOpen, LucideIcons.FontFamily),
        FileBrowserRow.Directory => (LucideIcons.Folder, LucideIcons.FontFamily),
        FileBrowserRow.Symbol symbol => (SymbolGlyph(symbol.Kind), LucideIcons.FontFamily),
        _ when row.IsLink => (LucideIcons.FileSymlink, LucideIcons.FontFamily),
        _ when LanguageMark(row.Name) is { } mark => (mark, SetiIcons.FontFamily),
        _ => (LucideIcons.File, LucideIcons.FontFamily),
    };

    /// <summary>
    /// A fixed column, the way the chevron has one, so every filename starts at the same x whichever
    /// font drew the icon. Both fonts advance a full em, so sizing the column from the measured
    /// advance instead would move the text by however much the glyph was scaled.
    /// </summary>
    private const float IconColumnWidth = 16f;

    /// <summary>
    /// Seti's glyphs sit smaller within their em than Lucide's — 0.77 against 0.93, measured off the
    /// two fonts — and being detailed marks rather than line icons they need a little more than
    /// parity to read at a glance. Tune this one number if they look off.
    /// </summary>
    private const float IconSize = FontSize.Body;
    private const float SetiIconSize = 17f;

    private static string? LanguageMark(string name) =>
        CodeLanguages.Detect(name) is { } language ? SetiIcons.For(language) : null;

    // Four categories, not fourteen: a reader scanning an outline is separating what runs from what
    // holds a value from what contains either, and a glyph per SymbolKind would be a legend.
    private static string SymbolGlyph(SymbolKind kind) => kind switch
    {
        SymbolKind.Namespace => LucideIcons.Braces,
        SymbolKind.Method or SymbolKind.Constructor or SymbolKind.Function => LucideIcons.FunctionSquare,
        SymbolKind.Property or SymbolKind.Field or SymbolKind.Event or SymbolKind.EnumMember =>
            LucideIcons.Variable,
        _ => LucideIcons.Box,
    };

    private static uint Tint(uint color, bool dim) =>
        dim ? (color & 0x00FFFFFFu) | (0x80u << 24) : color;

    private static RectF Place(in RectF rowRect, float left, float width, bool isRtl) =>
        isRtl
            ? new RectF(rowRect.Left + rowRect.Right - left - width, rowRect.Bottom, width, RowHeight)
            : new RectF(left, rowRect.Bottom, width, RowHeight);
}
