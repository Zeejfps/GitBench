using ZGF.Geometry;

namespace GitBench.Features.Markdown.Rendering;

/// <summary>
/// What a text leaf needs from the markdown surface it sits in: somewhere to announce itself for
/// its mounted lifetime, and somewhere to ask which of its characters are selected. Resolved from
/// the build context, so a <see cref="RichText"/> built outside any surface simply has no
/// selection.
/// </summary>
internal interface IMarkdownSelectionScope
{
    /// <summary>Enrols <paramref name="leaf"/> as a selectable leaf of this surface until the
    /// returned handle is disposed. A selection ending in a leaf that leaves is dropped, which is
    /// how a re-parsed block loses the highlight it carried.</summary>
    IDisposable Register(RichTextView leaf);

    /// <summary>The selected character range of <paramref name="leaf"/>, or false when none of it
    /// is selected.</summary>
    bool TrySpan(RichTextView leaf, out int from, out int to);
}

/// <summary>
/// The selectable text of one markdown surface: which leaves it currently has, what order they
/// read in, and the one selection running across them. One scope is one surface — one assistant
/// reply, one preview — so a selection can never reach into the next document.
/// </summary>
internal sealed class MarkdownSelectionScope : IMarkdownSelectionScope
{
    private readonly List<RichTextView> _leaves = new();

    public MarkdownSelectionModel Selection { get; } = new();

    public IDisposable Register(RichTextView leaf)
    {
        _leaves.Add(leaf);
        leaf.Selection = this;
        return new Registration(this, leaf);
    }

    public bool TrySpan(RichTextView leaf, out int from, out int to) =>
        Selection.TryLeafSpan(leaf, leaf.SelectableText.Length, out from, out to);

    /// <summary>The surface's leaves in document order. Recomputed on demand from their laid-out
    /// positions: streaming rebuilds slots in whatever order the deltas arrive, so registration
    /// order says nothing about reading order.</summary>
    public IReadOnlyList<RichTextView> DocumentOrder()
    {
        var ordered = new List<RichTextView>(_leaves);
        ordered.Sort(MarkdownTextPos.CompareLeaves);
        return ordered;
    }

    /// <summary>The caret position under <paramref name="point"/>, or null when it falls on no
    /// leaf — a table, a rule, the gap between blocks.</summary>
    public MarkdownTextPos? HitTest(PointF point)
    {
        foreach (var leaf in _leaves)
        {
            if (leaf.Position.ContainsPoint(point))
                return new MarkdownTextPos(leaf, leaf.CharIndexAt(point));
        }
        return null;
    }

    /// <summary>The nearest position inside this surface for a drag that has wandered off its
    /// leaves — across a table, past the last block, into the next reply. Null only while the
    /// surface has no leaves at all, so a drag keeps tracking wherever the cursor goes.</summary>
    public MarkdownTextPos? Clamp(PointF point)
    {
        RichTextView? nearest = null;
        var best = float.MaxValue;
        foreach (var leaf in _leaves)
        {
            var distance = leaf.Position.DistanceSqTo(point);
            if (distance >= best) continue;
            best = distance;
            nearest = leaf;
        }
        return nearest == null ? null : new MarkdownTextPos(nearest, nearest.CharIndexAt(point));
    }

    /// <summary>Redraws every leaf after the selection changed.</summary>
    public void Invalidate()
    {
        foreach (var leaf in _leaves)
            leaf.SelectionChanged();
    }

    private sealed class Registration : IDisposable
    {
        private readonly MarkdownSelectionScope _scope;
        private readonly RichTextView _leaf;

        public Registration(MarkdownSelectionScope scope, RichTextView leaf)
        {
            _scope = scope;
            _leaf = leaf;
        }

        public void Dispose()
        {
            _scope._leaves.Remove(_leaf);
            _leaf.Selection = null;
            if (_scope.Selection.DropLeaf(_leaf))
                _scope.Invalidate();
        }
    }
}
