namespace GitBench.Features.Markdown.Parsing;

/// <summary>
/// The parser-agnostic markdown AST — the seam's contract. Any parser backend produces these
/// records; the renderer, theming, and streaming diff all sit on them. Streaming requires
/// structural (value) equality across the whole tree, including the list-typed properties, so
/// two parses of the same text compare equal and unchanged blocks keep their views.
/// </summary>
internal sealed record MarkdownDocument(IReadOnlyList<MarkdownBlock> Blocks);

/// <summary>Base of every block-level node.</summary>
internal abstract record MarkdownBlock;

/// <summary>ATX heading, level 1–6.</summary>
internal sealed record HeadingBlock(int Level, IReadOnlyList<InlineRun> Runs) : MarkdownBlock;

/// <summary>A paragraph of inline runs. Raw line breaks survive in the run text for Step 2.</summary>
internal sealed record ParagraphBlock(IReadOnlyList<InlineRun> Runs) : MarkdownBlock;

/// <summary>
/// Fenced code block. <paramref name="Language"/> is the first word of the info string (null when
/// absent); <paramref name="Text"/> is the verbatim content, never inline-parsed;
/// <paramref name="IsClosed"/> is false when the fence is still open at end of input, which
/// streaming renders as an in-progress block.
/// </summary>
internal sealed record CodeBlock(string? Language, string Text, bool IsClosed) : MarkdownBlock;

/// <summary>Ordered or unordered list. <paramref name="Start"/> is the first item's number.</summary>
internal sealed record ListBlock(bool Ordered, int Start, IReadOnlyList<ListItem> Items) : MarkdownBlock;

/// <summary>
/// One list item: nested blocks plus the GFM task state — true/false for <c>[x]</c>/<c>[ ]</c>,
/// null for a plain item.
/// </summary>
internal sealed record ListItem(IReadOnlyList<MarkdownBlock> Blocks, bool? TaskChecked);

/// <summary>Blockquote wrapping nested blocks; quotes nest by containing another quote.</summary>
internal sealed record QuoteBlock(IReadOnlyList<MarkdownBlock> Blocks) : MarkdownBlock;

/// <summary>Thematic break (horizontal rule).</summary>
internal sealed record ThematicBreakBlock : MarkdownBlock;

/// <summary>
/// GFM pipe table. One <see cref="ColumnAlignment"/> per column from the delimiter row; a cell is
/// a run list, so <paramref name="Header"/> is one cell per column and <paramref name="Rows"/> is
/// one cell list per data row.
/// </summary>
internal sealed record TableBlock(
    IReadOnlyList<ColumnAlignment> Columns,
    IReadOnlyList<IReadOnlyList<InlineRun>> Header,
    IReadOnlyList<IReadOnlyList<IReadOnlyList<InlineRun>>> Rows) : MarkdownBlock;

/// <summary>Per-column alignment parsed from the table delimiter row.</summary>
internal enum ColumnAlignment
{
    None,
    Left,
    Center,
    Right,
}

/// <summary>
/// One flat, pre-resolved styled run — the AST's entire inline model. The parser flattens any
/// emphasis nesting; the renderer never sees a tree. In Step 1 (block parsing only) every
/// paragraph, heading, and cell holds a single unstyled run of raw inline text.
/// </summary>
internal sealed record InlineRun(
    string Text,
    bool Bold = false,
    bool Italic = false,
    bool Code = false,
    bool Strikethrough = false,
    string? LinkUrl = null);
