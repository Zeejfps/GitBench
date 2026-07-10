using System.Text;

namespace GitBench.Features.Diff;

/// <summary>
/// A caret position in a diff's row stream: a row index and a character offset into that row's
/// text. Offsets are into the pre-formatted <see cref="DiffRow.Line.Text"/> — tabs already
/// expanded — so a position means the same thing to the hit-test, the painter, and the clipboard.
/// </summary>
internal readonly record struct DiffTextPos(int Row, int Char) : IComparable<DiffTextPos>
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

/// <summary>The slice of one row's text that is selected, in character offsets.
/// <see cref="IncludesEol"/> marks a row whose line break falls inside the selection, so the
/// painter can extend the highlight past the last glyph the way editors do.</summary>
internal readonly record struct DiffRowSelection(int StartChar, int EndChar, bool IncludesEol);

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
    public bool TryRowSpan(object? scope, int row, int textLength, out DiffRowSelection span)
    {
        span = default;
        if (!HasRange || !Equals(Scope, scope)) return false;

        var start = Start;
        var end = End;
        if (row < start.Row || row > end.Row) return false;

        var from = row == start.Row ? Math.Clamp(start.Char, 0, textLength) : 0;
        var to = row == end.Row ? Math.Clamp(end.Char, 0, textLength) : textLength;
        if (to < from) return false;

        var includesEol = row < end.Row;
        if (from == to && !includesEol) return false;

        span = new DiffRowSelection(from, to, includesEol);
        return true;
    }

    /// <summary>
    /// The selected text, newline-joined. Only <see cref="DiffRow.Line"/> rows contribute: the
    /// clipboard gets the code as it would appear in the file, without the line-number gutters,
    /// the +/- glyph, or the "@@" separator bars a selection may drag across.
    /// </summary>
    public static string BuildCopyText(IReadOnlyList<DiffRow> rows, DiffTextPos start, DiffTextPos end)
    {
        var sb = new StringBuilder();
        var first = true;
        var last = Math.Min(end.Row, rows.Count - 1);
        for (var row = Math.Max(0, start.Row); row <= last; row++)
        {
            if (rows[row] is not DiffRow.Line line) continue;
            var text = line.Text;
            var from = row == start.Row ? Math.Clamp(start.Char, 0, text.Length) : 0;
            var to = row == end.Row ? Math.Clamp(end.Char, 0, text.Length) : text.Length;
            if (to < from) continue;

            if (!first) sb.Append('\n');
            sb.Append(text, from, to - from);
            first = false;
        }
        return sb.ToString();
    }

    /// <summary>The whole-rows span of a row list, for Select All.</summary>
    public static (DiffTextPos Start, DiffTextPos End)? WholeSpan(IReadOnlyList<DiffRow> rows)
    {
        if (rows.Count == 0) return null;
        var lastRow = rows.Count - 1;
        var lastLength = rows[lastRow] is DiffRow.Line line ? line.Text.Length : 0;
        return (new DiffTextPos(0, 0), new DiffTextPos(lastRow, lastLength));
    }

    /// <summary>The word around a position, or the whole run of whitespace it sits in. Falls back
    /// to a single character at a lone symbol so a double-click always selects something.</summary>
    public static (DiffTextPos Start, DiffTextPos End) WordSpan(IReadOnlyList<DiffRow> rows, DiffTextPos pos)
    {
        if (rows.Count == 0 || pos.Row < 0 || pos.Row >= rows.Count || rows[pos.Row] is not DiffRow.Line line)
            return (pos, pos);

        var text = line.Text;
        if (text.Length == 0) return (new DiffTextPos(pos.Row, 0), new DiffTextPos(pos.Row, 0));

        var at = Math.Clamp(pos.Char, 0, text.Length - 1);
        var kind = ClassOf(text[at]);
        var from = at;
        while (from > 0 && ClassOf(text[from - 1]) == kind) from--;
        var to = at + 1;
        while (to < text.Length && ClassOf(text[to]) == kind) to++;
        return (new DiffTextPos(pos.Row, from), new DiffTextPos(pos.Row, to));
    }

    /// <summary>The whole line a position sits on, including its trailing newline when another
    /// line follows — so a triple-click drag copies complete lines.</summary>
    public static (DiffTextPos Start, DiffTextPos End) LineSpan(IReadOnlyList<DiffRow> rows, DiffTextPos pos)
    {
        if (rows.Count == 0 || pos.Row < 0 || pos.Row >= rows.Count) return (pos, pos);
        var length = rows[pos.Row] is DiffRow.Line line ? line.Text.Length : 0;
        return (new DiffTextPos(pos.Row, 0), new DiffTextPos(pos.Row, length));
    }

    private enum CharClass { Whitespace, Word, Symbol }

    private static CharClass ClassOf(char c)
    {
        if (char.IsWhiteSpace(c)) return CharClass.Whitespace;
        if (char.IsLetterOrDigit(c) || c == '_') return CharClass.Word;
        return CharClass.Symbol;
    }
}
