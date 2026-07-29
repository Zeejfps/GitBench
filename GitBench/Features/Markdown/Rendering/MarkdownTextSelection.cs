using System.Text;

namespace GitBench.Features.Markdown.Rendering;

/// <summary>
/// A caret position in a rendered markdown document: the text leaf it falls in and a character
/// offset into that leaf's concatenated run text — the offsets the leaf hit-tests, paints and
/// copies in. Identity is the leaf itself rather than its index, because a streaming document
/// rebuilds its block slots out of order and only the surviving views mean anything.
/// <para>
/// Ordering between two leaves is geometric, taken from their laid-out positions: top-down, then
/// left-to-right (GUI coordinates are y-up). Nested leaves (a list item's paragraph, a quoted one)
/// and out-of-order rebuilds leave no structural order to read.
/// </para>
/// </summary>
internal readonly record struct MarkdownTextPos(RichTextView Leaf, int Char) : IComparable<MarkdownTextPos>
{
    public int CompareTo(MarkdownTextPos other)
    {
        var byLeaf = CompareLeaves(Leaf, other.Leaf);
        return byLeaf != 0 ? byLeaf : Char.CompareTo(other.Char);
    }

    /// <summary>Document order between two text leaves of the same surface.</summary>
    public static int CompareLeaves(RichTextView a, RichTextView b)
    {
        if (ReferenceEquals(a, b)) return 0;
        var byTop = b.Position.Top.CompareTo(a.Position.Top);
        return byTop != 0 ? byTop : a.Position.Left.CompareTo(b.Position.Left);
    }

    public static bool operator <(MarkdownTextPos a, MarkdownTextPos b) => a.CompareTo(b) < 0;
    public static bool operator >(MarkdownTextPos a, MarkdownTextPos b) => a.CompareTo(b) > 0;
    public static bool operator <=(MarkdownTextPos a, MarkdownTextPos b) => a.CompareTo(b) <= 0;
    public static bool operator >=(MarkdownTextPos a, MarkdownTextPos b) => a.CompareTo(b) >= 0;
}

/// <summary>
/// Anchor/focus text selection over the leaves of one markdown surface. Held by a
/// <see cref="MarkdownSelectionScope"/> and driven by <see cref="MarkdownSelectionController"/>;
/// each leaf reads its own slice back through <see cref="TryLeafSpan"/> when it paints.
///
/// Mutators return true when something actually changed, so callers only repaint on real edits.
/// </summary>
internal sealed class MarkdownSelectionModel
{
    public MarkdownTextPos Anchor { get; private set; }
    public MarkdownTextPos Focus { get; private set; }

    /// <summary>A selection exists, possibly collapsed (a plain click before any drag).</summary>
    public bool IsActive { get; private set; }

    /// <summary>A selection exists and covers at least one character.</summary>
    public bool HasRange => IsActive && Anchor != Focus;

    public MarkdownTextPos Start => Anchor <= Focus ? Anchor : Focus;
    public MarkdownTextPos End => Anchor <= Focus ? Focus : Anchor;

    public void Begin(MarkdownTextPos pos)
    {
        Anchor = Focus = pos;
        IsActive = true;
    }

    public bool ExtendTo(MarkdownTextPos pos)
    {
        if (!IsActive || Focus == pos) return false;
        Focus = pos;
        return true;
    }

    public bool Clear()
    {
        if (!IsActive) return false;
        IsActive = false;
        Anchor = Focus = default;
        return true;
    }

    /// <summary>Drops a selection that ends in <paramref name="leaf"/> because that leaf is going
    /// away: the block it covered was re-parsed, and a highlight over text that has moved would be
    /// a lie.</summary>
    public bool DropLeaf(RichTextView leaf)
    {
        if (!IsActive) return false;
        if (!ReferenceEquals(Anchor.Leaf, leaf) && !ReferenceEquals(Focus.Leaf, leaf)) return false;
        return Clear();
    }

    /// <summary>
    /// The selected slice of <paramref name="leaf"/>, or false when none of it is selected.
    /// <paramref name="textLength"/> clamps offsets captured against a since-rewrapped leaf.
    /// </summary>
    public bool TryLeafSpan(RichTextView leaf, int textLength, out int from, out int to)
    {
        from = to = 0;
        return HasRange && Slice(leaf, textLength, Start, End, out from, out to);
    }

    /// <summary>
    /// The selected text, leaves newline-joined. What comes out is what the reader can see: the
    /// heading without its '#', the bold word without its asterisks. Blocks that register no leaf —
    /// a table, a thematic break — contribute nothing, the way the diff's "@@" bars do.
    /// </summary>
    public static string BuildCopyText(
        IReadOnlyList<RichTextView> leaves, MarkdownTextPos start, MarkdownTextPos end)
    {
        var sb = new StringBuilder();
        foreach (var leaf in leaves)
        {
            var text = leaf.SelectableText;
            if (!Slice(leaf, text.Length, start, end, out var from, out var to)) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(text, from, to - from);
        }
        return sb.ToString();
    }

    /// <summary>The one rule for how much of a leaf a span covers, shared by the painter and the
    /// clipboard so a highlight and its copy can never disagree: the two end leaves are cut at
    /// their offsets, every leaf between them is taken whole, and one outside contributes
    /// nothing.</summary>
    private static bool Slice(
        RichTextView leaf,
        int textLength,
        MarkdownTextPos start,
        MarkdownTextPos end,
        out int from,
        out int to)
    {
        from = to = 0;
        if (MarkdownTextPos.CompareLeaves(leaf, start.Leaf) < 0) return false;
        if (MarkdownTextPos.CompareLeaves(leaf, end.Leaf) > 0) return false;

        from = ReferenceEquals(leaf, start.Leaf) ? Math.Clamp(start.Char, 0, textLength) : 0;
        to = ReferenceEquals(leaf, end.Leaf) ? Math.Clamp(end.Char, 0, textLength) : textLength;
        return to > from;
    }
}
