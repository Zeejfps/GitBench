using GitBench.Features.Markdown.Parsing;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Markdown.Rendering;

/// <summary>
/// The streaming markdown surface: binds a <see cref="MarkdownBlockList"/>'s
/// <see cref="MarkdownBlockList.Blocks"/> with <c>Each</c> so each block row builds its own view
/// and — because <c>Each</c>'s children binding replays only the list's fine-grained events —
/// every slot the streaming diff leaves untouched keeps its existing view (and layout caches)
/// across text updates. Contract pinned by <c>MarkdownStreamTests</c>:
/// <list type="bullet">
/// <item><description>The rendered output always shows the current blocks, matching what
/// <see cref="MarkdownWidget"/> renders for the same document — per-block visuals are Step 5/6
/// territory and are pinned there, not here.</description></item>
/// <item><description>A view belonging to an untouched slot survives (reference-identical in the
/// tree) when later blocks are appended or the last block is replaced.</description></item>
/// <item><description>In-progress constructs render as their eventual shape: an open fence is a
/// live code block, a header+delimiter-only table is a table.</description></item>
/// </list>
/// The column shape (gap, stretch) matches <see cref="MarkdownWidget"/> so static and streamed
/// documents look identical; each row renders through the same
/// <see cref="MarkdownWidget.BlockWidget"/> dispatch.
/// </summary>
internal sealed record MarkdownStream : Widget
{
    /// <summary>The streaming model to render. The caller owns it (the assistant's streamed-turn
    /// VM feeds it); this widget only binds it.</summary>
    public required MarkdownBlockList Source { get; init; }

    protected override IWidget Build(Context ctx) =>
        Each.Of(Source.Blocks, new BlockRow(), gap: Spacing.Md)
            with { CrossAxis = CrossAxisAlignment.Stretch };

    /// <summary>The per-row template: resolves its block from the item scope <c>Each</c> creates
    /// and defers to <see cref="MarkdownWidget.BlockWidget"/>. A row's view is only ever rebuilt
    /// when its slot receives a list event, which is what keeps untouched blocks' views (and their
    /// layout caches) alive while the tail streams.</summary>
    private sealed record BlockRow : Widget
    {
        protected override IWidget Build(Context ctx) =>
            MarkdownWidget.BlockWidget(ctx.Require<MarkdownBlock>(), static s => s.Palette.TextBody)
            // Unknown blocks degrade to an empty row (MarkdownWidget skips them; Each needs one
            // view per slot) — the parser produces none today.
            ?? new Column();
    }
}
