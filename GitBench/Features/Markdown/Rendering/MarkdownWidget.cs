using GitBench.Features.Markdown.Parsing;
using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Markdown.Rendering;

/// <summary>
/// Renders a parsed <see cref="MarkdownDocument"/> as a <c>Column</c> of block widgets — the
/// reusable markdown surface the assistant transcript (and anything else markdown-shaped)
/// embeds. Static per document: streaming identity/diffing is Step 7's <c>MarkdownBlockList</c>,
/// which rebuilds this widget's rows, not this widget's concern.
/// <para>
/// Block mapping (pinned by <c>MarkdownWidgetTests</c>):
/// headings → bold <see cref="RichText"/> on the fixed FontSize ladder
/// (H1 = Title 22, H2 = Heading 16, H3 = Default 14, H4–H6 = Body 13), heading text in
/// <c>Palette.TextStrong</c>; paragraphs → <see cref="RichText"/> at Body 13 in
/// <c>Palette.TextBody</c>, inline styling via <see cref="InlineRunBuilder"/>; lists → marker
/// gutter ("•" bullets, "n." numbers honoring <c>Start</c>, display-only Lucide
/// square/check-square glyphs for task items) plus nested, indented children; blockquotes →
/// themed accent bar + inset children, nesting stacks bars/insets; thematic break → thin themed
/// rule; code blocks → <see cref="CodeBlockWidget"/>. <c>TableBlock</c> is Step 6: it renders as
/// a skipped block (no output) — pinned only as "a document containing a table renders its other
/// blocks and never throws", so Step 6 can drop the real table in without touching Step 5 tests.
/// </para>
/// <para>
/// Context: theme via <c>ctx.Theme()</c>, localization via <c>L.T</c> (copy button), optional
/// <c>IPlatformShell</c>/<c>IClipboard</c> exactly as <see cref="RichText"/> and
/// <see cref="CodeBlockWidget"/> resolve them.
/// </para>
/// </summary>
internal sealed record MarkdownWidget : Widget
{
    /// <summary>The parsed document to render.</summary>
    public required MarkdownDocument Document { get; init; }

    protected override IWidget Build(Context ctx)
    {
        throw new NotImplementedException("Step 5: MarkdownWidget.Build is not implemented yet.");
    }
}
