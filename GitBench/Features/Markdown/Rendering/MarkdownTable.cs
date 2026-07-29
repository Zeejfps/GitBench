using GitBench.Controls;
using GitBench.Features.Markdown.Parsing;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Markdown.Rendering;

/// <summary>
/// Widget wrapper over <see cref="MarkdownTableView"/> — how <see cref="MarkdownWidget"/> renders
/// a <see cref="TableBlock"/> (Step 6 replaces the Step 5 skipped-block placeholder).
/// <para>
/// Contract, pinned by <c>MarkdownWidgetTableTests</c>: the view is always nested in a
/// <c>HorizontalScrollArea</c> (the code-block precedent) — because the view's intrinsic width is
/// its min-content total, the scroll view lays the table out at the viewport width whenever
/// min-content fits (so cells wrap and columns distribute), and at min widths with horizontal
/// scrolling only when even Σmin overflows. Cell runs are built with
/// <see cref="InlineRunBuilder"/> against the live theme: header cells bold
/// (<c>bold: true</c>) in <c>Palette.TextStrong</c>, body cells at <c>FontSize.Body</c> in
/// <c>Palette.TextBody</c>, so cell inline styling (code chips, italics, links' colors) matches
/// paragraphs. Alignments pass through from the block. Rule colors come from the theme's
/// <c>MarkdownStyles.TableHeaderRule</c>/<c>TableRowSeparator</c> slots and restyle on a live
/// theme flip like every other markdown surface.
/// </para>
/// </summary>
internal sealed record MarkdownTable : Widget
{
    /// <summary>The parsed table to render.</summary>
    public required TableBlock Block { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var block = Block;
        return new HorizontalScrollArea
        {
            Child = new TableContent
            {
                Alignments = block.Columns,
                Header = Theme.Styled<IReadOnlyList<IReadOnlyList<RichTextRun>>>(
                    s => HeaderCells(block, s)),
                Rows = Theme.Styled<IReadOnlyList<IReadOnlyList<IReadOnlyList<RichTextRun>>>>(
                    s => RowCells(block, s)),
                HeaderRuleColor = Theme.Color(s => s.Markdown.TableHeaderRule),
                RowSeparatorColor = Theme.Color(s => s.Markdown.TableRowSeparator),
                CodeChipBackground = Theme.Color(s => s.Markdown.CodeChipBackground),
            },
        };
    }

    /// <summary>Header cells as bold <c>TextStrong</c> runs — rebuilt per theme flip so the
    /// colors track the active palette.</summary>
    private static IReadOnlyList<IReadOnlyList<RichTextRun>> HeaderCells(
        TableBlock block, ThemeStyles styles)
    {
        var cells = new IReadOnlyList<RichTextRun>[block.Header.Count];
        for (var j = 0; j < cells.Length; j++)
        {
            cells[j] = InlineRunBuilder.Build(
                block.Header[j], styles.Markdown, FontSize.Body, styles.Palette.TextStrong,
                bold: true);
        }
        return cells;
    }

    /// <summary>Body cells as plain <c>TextBody</c> runs at Body size, inline styling flowing
    /// through <see cref="InlineRunBuilder"/> exactly like paragraphs.</summary>
    private static IReadOnlyList<IReadOnlyList<IReadOnlyList<RichTextRun>>> RowCells(
        TableBlock block, ThemeStyles styles)
    {
        var rows = new IReadOnlyList<IReadOnlyList<RichTextRun>>[block.Rows.Count];
        for (var i = 0; i < rows.Length; i++)
        {
            var row = block.Rows[i];
            var cells = new IReadOnlyList<RichTextRun>[row.Count];
            for (var j = 0; j < cells.Length; j++)
            {
                cells[j] = InlineRunBuilder.Build(
                    row[j], styles.Markdown, FontSize.Body, styles.Palette.TextBody);
            }
            rows[i] = cells;
        }
        return rows;
    }

    /// <summary>The scroll area's content: constructs the view and applies the theme-bound props,
    /// mirroring <see cref="RichText"/>'s CreateView shape.</summary>
    private sealed record TableContent : Widget
    {
        public required IReadOnlyList<ColumnAlignment> Alignments { get; init; }
        public Prop<IReadOnlyList<IReadOnlyList<RichTextRun>>> Header { get; init; }
        public Prop<IReadOnlyList<IReadOnlyList<IReadOnlyList<RichTextRun>>>> Rows { get; init; }
        public Prop<uint> HeaderRuleColor { get; init; }
        public Prop<uint> RowSeparatorColor { get; init; }
        public Prop<uint> CodeChipBackground { get; init; }

        protected override View CreateView(Context ctx)
        {
            var view = new MarkdownTableView(ctx.Canvas) { Alignments = Alignments };
            Header.Apply(ctx, view, static (v, cells) => v.Header = cells);
            Rows.Apply(ctx, view, static (v, rows) => v.Rows = rows);
            HeaderRuleColor.Apply(ctx, view, static (v, color) => v.HeaderRuleColor = color);
            RowSeparatorColor.Apply(ctx, view, static (v, color) => v.RowSeparatorColor = color);
            CodeChipBackground.Apply(ctx, view, static (v, color) => v.CodeChipBackground = color);
            return view;
        }
    }
}
