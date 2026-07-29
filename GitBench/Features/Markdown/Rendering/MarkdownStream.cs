using ZGF.Gui;
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
/// Implementation notes (implementer's latitude): the per-item template resolves its
/// <see cref="Parsing.MarkdownBlock"/> from the item scope <c>Each</c> creates
/// (<c>ctx.Require&lt;MarkdownBlock&gt;()</c>) and should reuse <see cref="MarkdownWidget"/>'s
/// block dispatch — exposing that per-block mapping as an internal helper on
/// <see cref="MarkdownWidget"/> is an accepted refactor. Match <see cref="MarkdownWidget"/>'s
/// column shape (gap, stretch) so static and streamed documents look identical.
/// </summary>
internal sealed record MarkdownStream : Widget
{
    /// <summary>The streaming model to render. The caller owns it (the assistant's streamed-turn
    /// VM feeds it); this widget only binds it.</summary>
    public required MarkdownBlockList Source { get; init; }

    protected override IWidget Build(Context ctx) =>
        throw new NotImplementedException("Step 7: MarkdownStream");
}
