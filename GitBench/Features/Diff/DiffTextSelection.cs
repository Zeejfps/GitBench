using System.Text;

namespace GitBench.Features.Diff;

/// <summary>
/// A caret position in a diff's row stream: a row index and a column into that row's rendered
/// text. The column is in expanded space — the space the hit-test and the painter share — and the
/// clipboard maps it back to the file's own characters through <see cref="DiffLineText"/>.
/// </summary>
internal readonly record struct DiffTextPos(RowIndex Row, ExpandedColumn Char) : IComparable<DiffTextPos>
{
    public int CompareTo(DiffTextPos other) =>
        Row != other.Row ? Row.CompareTo(other.Row) : Char.CompareTo(other.Char);

    public static bool operator <(DiffTextPos a, DiffTextPos b) => a.CompareTo(b) < 0;
    public static bool operator >(DiffTextPos a, DiffTextPos b) => a.CompareTo(b) > 0;
    public static bool operator <=(DiffTextPos a, DiffTextPos b) => a.CompareTo(b) <= 0;
    public static bool operator >=(DiffTextPos a, DiffTextPos b) => a.CompareTo(b) >= 0;
}

/// <summary>
/// A position resolved from a pointer, tagged with the scope that owns it. The scope is null for
/// a single-file diff body and the file path for the review list, whose one scrolling surface
/// stacks many files — a selection may never span two of them.
/// </summary>
internal readonly record struct DiffTextHit(object? Scope, DiffTextPos Pos);

/// <summary>The selected slice of one row, in the expanded columns the painter draws in.
/// <see cref="IncludesEol"/> marks a row whose line break falls inside the selection, so the
/// painter can extend the highlight past the last glyph the way editors do.</summary>
internal readonly record struct DiffRowSelection(
    ExpandedColumn StartChar, ExpandedColumn EndChar, bool IncludesEol);

/// <summary>
/// Anchor/focus text selection over a diff's rows, scoped to one file. Held by a diff surface
/// (<see cref="DiffContentView"/>, the review list) and driven by <see cref="DiffSelectionController"/>;
/// the painter reads it back per row through <see cref="TryRowSpan"/>.
///
/// Mutators return true when something actually changed, so callers only repaint on real edits.
/// </summary>
internal sealed class DiffSelectionModel
{
    public object? Scope { get; private set; }
    public DiffTextPos Anchor { get; private set; }
    public DiffTextPos Focus { get; private set; }

    /// <summary>A selection exists, possibly collapsed (a plain click before any drag).</summary>
    public bool IsActive { get; private set; }

    /// <summary>A selection exists and covers at least one character.</summary>
    public bool HasRange => IsActive && Anchor != Focus;

    public DiffTextPos Start => Anchor <= Focus ? Anchor : Focus;
    public DiffTextPos End => Anchor <= Focus ? Focus : Anchor;

    public void Begin(object? scope, DiffTextPos pos)
    {
        Scope = scope;
        Anchor = Focus = pos;
        IsActive = true;
    }

    public bool SetRange(object? scope, DiffTextPos anchor, DiffTextPos focus)
    {
        if (IsActive && Equals(Scope, scope) && Anchor == anchor && Focus == focus) return false;
        Scope = scope;
        Anchor = anchor;
        Focus = focus;
        IsActive = true;
        return true;
    }

    /// <summary>Moves the focus end. Ignores positions from another scope — a drag that wanders
    /// onto a different file's card must not swallow it.</summary>
    public bool ExtendTo(object? scope, DiffTextPos pos)
    {
        if (!IsActive || !Equals(Scope, scope) || Focus == pos) return false;
        Focus = pos;
        return true;
    }

    public bool Clear()
    {
        if (!IsActive) return false;
        IsActive = false;
        Scope = null;
        Anchor = Focus = default;
        return true;
    }

    /// <summary>
    /// The selected slice of the given row, or false when the row lies outside the selection.
    /// <paramref name="textLength"/> clamps positions captured against a since-rebuilt row.
    /// </summary>
    public bool TryRowSpan(object? scope, RowIndex row, ExpandedColumn textLength, out DiffRowSelection span)
    {
        span = default;
        if (!HasRange || !Equals(Scope, scope)) return false;

        var start = Start;
        var end = End;
        if (row < start.Row || row > end.Row) return false;

        var from = row == start.Row ? Clamp(start.Char, textLength) : default;
        var to = row == end.Row ? Clamp(end.Char, textLength) : textLength;
        if (to < from) return false;

        var includesEol = row < end.Row;
        if (from == to && !includesEol) return false;

        span = new DiffRowSelection(from, to, includesEol);
        return true;
    }

    /// <summary>
    /// The selected text, newline-joined. Only <see cref="DiffRow.Line"/> rows contribute: the
    /// clipboard gets the code as it would appear in the file — raw text, tabs and all, without
    /// the line-number gutters, the +/- glyph, or the "@@" separator bars a selection may drag
    /// across.
    /// </summary>
    /// <param name="hiddenAfter">The raw text a collapsed fold swallowed after a row, if anything.
    /// A selection that runs past such a row covers the body it hides, so the body has to come
    /// with it — text the reader could not see is still text they selected, and dropping it
    /// silently would be a copy that lies about the file.</param>
    public static string BuildCopyText(
        IReadOnlyList<DiffRow> rows, DiffTextPos start, DiffTextPos end, Func<RowIndex, string?>? hiddenAfter = null)
    {
        var sb = new StringBuilder();
        var first = true;
        var last = Math.Min(end.Row.Value, rows.Count - 1);
        for (var i = Math.Max(0, start.Row.Value); i <= last; i++)
        {
            if (rows[i] is not DiffRow.Line line) continue;
            var row = new RowIndex(i);
            var text = line.Text;
            var from = row == start.Row ? Clamp(start.Char, text.End) : default;
            var to = row == end.Row ? Clamp(end.Char, text.End) : text.End;
            if (to < from) continue;

            if (!first) sb.Append('\n');
            sb.Append(text.RawSlice(from, to));
            first = false;

            // Only when the selection actually continues past this row: ending on a fold header
            // selects the header, not the body behind it.
            if (row < end.Row && hiddenAfter?.Invoke(row) is { } swallowed)
                sb.Append('\n').Append(swallowed);
        }
        return sb.ToString();
    }

    /// <summary>The whole-rows span of a row list, for Select All.</summary>
    public static (DiffTextPos Start, DiffTextPos End)? WholeSpan(IReadOnlyList<DiffRow> rows)
    {
        if (rows.Count == 0) return null;
        var lastRow = rows.Count - 1;
        var lastLength = rows[lastRow] is DiffRow.Line line ? line.Text.End : default;
        return (new DiffTextPos(default, default), new DiffTextPos(new RowIndex(lastRow), lastLength));
    }

    /// <summary>The word around a position, or the whole run of whitespace it sits in. Falls back
    /// to a single character at a lone symbol so a double-click always selects something.</summary>
    public static (DiffTextPos Start, DiffTextPos End) WordSpan(IReadOnlyList<DiffRow> rows, DiffTextPos pos)
    {
        if (rows.Count == 0 || pos.Row.Value < 0 || pos.Row.Value >= rows.Count
            || rows[pos.Row.Value] is not DiffRow.Line line)
            return (pos, pos);

        var text = line.Text.Expanded;
        if (text.Length == 0) return (new DiffTextPos(pos.Row, default), new DiffTextPos(pos.Row, default));

        var at = Math.Clamp(pos.Char.Value, 0, text.Length - 1);
        var kind = ClassOf(text[at]);
        var from = at;
        while (from > 0 && ClassOf(text[from - 1]) == kind) from--;
        var to = at + 1;
        while (to < text.Length && ClassOf(text[to]) == kind) to++;
        return (
            new DiffTextPos(pos.Row, new ExpandedColumn(from)),
            new DiffTextPos(pos.Row, new ExpandedColumn(to)));
    }

    /// <summary>The whole line a position sits on, including its trailing newline when another
    /// line follows — so a triple-click drag copies complete lines.</summary>
    public static (DiffTextPos Start, DiffTextPos End) LineSpan(IReadOnlyList<DiffRow> rows, DiffTextPos pos)
    {
        if (rows.Count == 0 || pos.Row.Value < 0 || pos.Row.Value >= rows.Count) return (pos, pos);
        var length = rows[pos.Row.Value] is DiffRow.Line line ? line.Text.End : default;
        return (new DiffTextPos(pos.Row, default), new DiffTextPos(pos.Row, length));
    }

    private static ExpandedColumn Clamp(ExpandedColumn column, ExpandedColumn end) =>
        new(Math.Clamp(column.Value, 0, end.Value));

    private enum CharClass { Whitespace, Word, Symbol }

    private static CharClass ClassOf(char c)
    {
        if (char.IsWhiteSpace(c)) return CharClass.Whitespace;
        if (char.IsLetterOrDigit(c) || c == '_') return CharClass.Word;
        return CharClass.Symbol;
    }
}
