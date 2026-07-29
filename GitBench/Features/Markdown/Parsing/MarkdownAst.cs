namespace GitBench.Features.Markdown.Parsing;

/// <summary>
/// The parser-agnostic markdown AST — the seam's contract. Any parser backend produces these
/// records; the renderer, theming, and streaming diff all sit on them. Streaming requires
/// structural (value) equality across the whole tree, including the list-typed properties, so
/// two parses of the same text compare equal and unchanged blocks keep their views.
/// </summary>
internal sealed record MarkdownDocument(IReadOnlyList<MarkdownBlock> Blocks)
{
    public bool Equals(MarkdownDocument? other)
        => other is not null && AstEquality.ListsEqual(Blocks, other.Blocks);

    public override int GetHashCode() => AstEquality.ListHash(Blocks);
}

/// <summary>Base of every block-level node.</summary>
internal abstract record MarkdownBlock;

/// <summary>ATX heading, level 1–6.</summary>
internal sealed record HeadingBlock(int Level, IReadOnlyList<InlineRun> Runs) : MarkdownBlock
{
    public bool Equals(HeadingBlock? other)
        => other is not null && Level == other.Level && AstEquality.ListsEqual(Runs, other.Runs);

    public override int GetHashCode() => HashCode.Combine(Level, AstEquality.ListHash(Runs));
}

/// <summary>A paragraph of inline runs. Raw line breaks survive in the run text for Step 2.</summary>
internal sealed record ParagraphBlock(IReadOnlyList<InlineRun> Runs) : MarkdownBlock
{
    public bool Equals(ParagraphBlock? other)
        => other is not null && AstEquality.ListsEqual(Runs, other.Runs);

    public override int GetHashCode() => AstEquality.ListHash(Runs);
}

/// <summary>
/// Fenced code block. <paramref name="Language"/> is the first word of the info string (null when
/// absent); <paramref name="Text"/> is the verbatim content, never inline-parsed;
/// <paramref name="IsClosed"/> is false when the fence is still open at end of input, which
/// streaming renders as an in-progress block.
/// </summary>
internal sealed record CodeBlock(string? Language, string Text, bool IsClosed) : MarkdownBlock;

/// <summary>Ordered or unordered list. <paramref name="Start"/> is the first item's number.</summary>
internal sealed record ListBlock(bool Ordered, int Start, IReadOnlyList<ListItem> Items) : MarkdownBlock
{
    public bool Equals(ListBlock? other)
        => other is not null
           && Ordered == other.Ordered
           && Start == other.Start
           && AstEquality.ListsEqual(Items, other.Items);

    public override int GetHashCode() => HashCode.Combine(Ordered, Start, AstEquality.ListHash(Items));
}

/// <summary>
/// One list item: nested blocks plus the GFM task state — true/false for <c>[x]</c>/<c>[ ]</c>,
/// null for a plain item.
/// </summary>
internal sealed record ListItem(IReadOnlyList<MarkdownBlock> Blocks, bool? TaskChecked)
{
    public bool Equals(ListItem? other)
        => other is not null && TaskChecked == other.TaskChecked && AstEquality.ListsEqual(Blocks, other.Blocks);

    public override int GetHashCode() => HashCode.Combine(TaskChecked, AstEquality.ListHash(Blocks));
}

/// <summary>Blockquote wrapping nested blocks; quotes nest by containing another quote.</summary>
internal sealed record QuoteBlock(IReadOnlyList<MarkdownBlock> Blocks) : MarkdownBlock
{
    public bool Equals(QuoteBlock? other)
        => other is not null && AstEquality.ListsEqual(Blocks, other.Blocks);

    public override int GetHashCode() => AstEquality.ListHash(Blocks);
}

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
    IReadOnlyList<IReadOnlyList<IReadOnlyList<InlineRun>>> Rows) : MarkdownBlock
{
    public bool Equals(TableBlock? other)
        => other is not null
           && AstEquality.ListsEqual(Columns, other.Columns)
           && AstEquality.CellsEqual(Header, other.Header)
           && AstEquality.RowsEqual(Rows, other.Rows);

    public override int GetHashCode() => HashCode.Combine(
        AstEquality.ListHash(Columns),
        AstEquality.CellsHash(Header),
        AstEquality.RowsHash(Rows));
}

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
/// emphasis nesting; the renderer never sees a tree. A hard line break is a dedicated run whose
/// <paramref name="Text"/> is exactly "\n", always unstyled and never merged into neighbors.
/// </summary>
internal sealed record InlineRun(
    string Text,
    bool Bold = false,
    bool Italic = false,
    bool Code = false,
    bool Strikethrough = false,
    string? LinkUrl = null);

/// <summary>
/// Sequence equality/hash for the AST's list-typed properties. Record equality compares the
/// element types by value (blocks route through their own overrides), so a flat element-wise
/// walk gives whole-tree structural equality; the nested table shapes stack the comparer-taking
/// overloads because a cell list's elements are themselves lists.
/// </summary>
internal static class AstEquality
{
    public static bool ListsEqual<T>(IReadOnlyList<T> a, IReadOnlyList<T> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        var comparer = EqualityComparer<T>.Default;
        for (var i = 0; i < a.Count; i++)
        {
            if (!comparer.Equals(a[i], b[i])) return false;
        }
        return true;
    }

    public static bool ListsEqual<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, Func<T, T, bool> equals)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!equals(a[i], b[i])) return false;
        }
        return true;
    }

    public static int ListHash<T>(IReadOnlyList<T> list)
    {
        var hash = new HashCode();
        for (var i = 0; i < list.Count; i++)
        {
            hash.Add(list[i]);
        }
        return hash.ToHashCode();
    }

    public static int ListHash<T>(IReadOnlyList<T> list, Func<T, int> hashOf)
    {
        var hash = new HashCode();
        for (var i = 0; i < list.Count; i++)
        {
            hash.Add(hashOf(list[i]));
        }
        return hash.ToHashCode();
    }

    public static bool CellsEqual(
        IReadOnlyList<IReadOnlyList<InlineRun>> a, IReadOnlyList<IReadOnlyList<InlineRun>> b)
        => ListsEqual(a, b, ListsEqual);

    public static int CellsHash(IReadOnlyList<IReadOnlyList<InlineRun>> cells)
        => ListHash(cells, ListHash);

    public static bool RowsEqual(
        IReadOnlyList<IReadOnlyList<IReadOnlyList<InlineRun>>> a,
        IReadOnlyList<IReadOnlyList<IReadOnlyList<InlineRun>>> b)
        => ListsEqual(a, b, CellsEqual);

    public static int RowsHash(IReadOnlyList<IReadOnlyList<IReadOnlyList<InlineRun>>> rows)
        => ListHash(rows, CellsHash);
}
