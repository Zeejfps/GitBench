using GitBench.Features.Markdown.Parsing;
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
        throw new NotImplementedException("Step 6: MarkdownTable.Build");
    }
}
