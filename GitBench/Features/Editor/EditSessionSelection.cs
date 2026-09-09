using GitBench.Features.Diff;

namespace GitBench.Features.Editor;

/// <summary>A column measured in monospace cells, not UTF-16 offsets: a tab is as wide as the tab
/// width says and a CJK glyph is two.</summary>
internal readonly record struct CellColumn(int Value) : IComparable<CellColumn>
{
    public int CompareTo(CellColumn other) => Value.CompareTo(other.Value);
}

/// <summary>A selection as the caret sees it: where it was put down and where it is now. Unlike
/// <see cref="TextRange"/> it keeps which end a Shift motion drags.</summary>
internal readonly record struct SelectionRange(TextPosition Anchor, TextPosition Caret)
{
    public static SelectionRange At(TextPosition position) => new(position, position);

    /// <summary>Reads a range back as a selection, taking its end as the caret.</summary>
    public static SelectionRange Of(TextRange range) => new(range.Start, range.End);

    /// <summary>The characters covered, in document order.</summary>
    public TextRange Range => Caret < Anchor ? new TextRange(Caret, Anchor) : new TextRange(Anchor, Caret);

    public bool IsEmpty => Anchor == Caret;

    public SelectionRange To(TextPosition caret, SelectionIntent intent) =>
        intent == SelectionIntent.Extend ? new SelectionRange(Anchor, caret) : At(caret);

    public override string ToString() => IsEmpty ? $"[{Caret}]" : $"[{Anchor}->{Caret}]";
}
