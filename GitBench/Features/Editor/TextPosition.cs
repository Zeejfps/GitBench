using GitBench.Features.Diff;

namespace GitBench.Features.Editor;

/// <summary>A point between two characters of a document: a 1-based <see cref="FileLine"/> and a
/// 0-based <see cref="RawColumn"/> into that line's own characters, its terminator excluded.</summary>
internal readonly record struct TextPosition(FileLine Line, RawColumn Column) : IComparable<TextPosition>
{
    public static TextPosition At(int line, int column) => new(new FileLine(line), new RawColumn(column));

    public int CompareTo(TextPosition other)
    {
        var line = Line.CompareTo(other.Line);
        return line != 0 ? line : Column.Value.CompareTo(other.Column.Value);
    }

    public override string ToString() => $"{Line.Value}:{Column.Value}";

    public static bool operator <(TextPosition a, TextPosition b) => a.CompareTo(b) < 0;
    public static bool operator >(TextPosition a, TextPosition b) => a.CompareTo(b) > 0;
    public static bool operator <=(TextPosition a, TextPosition b) => a.CompareTo(b) <= 0;
    public static bool operator >=(TextPosition a, TextPosition b) => a.CompareTo(b) >= 0;
}

/// <summary>A half-open span of a document; a caret is one whose ends are equal.
/// <see cref="Start"/> never follows <see cref="End"/> — the constructor refuses it.</summary>
internal readonly record struct TextRange
{
    public TextRange(TextPosition start, TextPosition end)
    {
        if (end < start)
            throw new ArgumentException($"A range cannot end ({end}) before it starts ({start}).", nameof(end));

        Start = start;
        End = end;
    }

    public static TextRange Caret(TextPosition at) => new(at, at);

    public TextPosition Start { get; }
    public TextPosition End { get; }

    public bool IsEmpty => Start == End;

    public void Deconstruct(out TextPosition start, out TextPosition end)
    {
        start = Start;
        end = End;
    }

    public override string ToString() => IsEmpty ? $"[{Start}]" : $"[{Start}..{End})";
}

/// <summary>The single unit of change: replace <see cref="Range"/> with <see cref="Replacement"/>.</summary>
internal readonly record struct TextEdit
{
    private readonly string? _replacement;

    public TextEdit(TextRange range, string replacement)
    {
        Range = range;
        _replacement = replacement ?? throw new ArgumentNullException(nameof(replacement));
    }

    public TextRange Range { get; }

    public string Replacement => _replacement ?? string.Empty;

    public void Deconstruct(out TextRange range, out string replacement)
    {
        range = Range;
        replacement = Replacement;
    }

    public override string ToString() => $"{Range} <- {Replacement.Length} chars";

    /// <summary>Where <paramref name="position"/> ends up once the edit has been applied. A position
    /// inside the replaced range collapses to its start; one exactly at the start follows the bias.</summary>
    public static TextPosition Shift(TextEdit edit, TextPosition position, AnchorBias bias)
    {
        var (start, end) = edit.Range;
        if (position < start) return position;

        var replacedEnd = EndOfReplacement(edit);
        if (position == start)
            return bias == AnchorBias.After ? replacedEnd : start;

        if (position < end) return start;

        if (position.Line == end.Line)
            return new TextPosition(replacedEnd.Line, new RawColumn(replacedEnd.Column.Value + position.Column.Value - end.Column.Value));

        return position with { Line = new FileLine(position.Line.Value + replacedEnd.Line.Value - end.Line.Value) };
    }

    /// <summary>Where a selection ends up once the edit has been applied. A selection the edit did
    /// not leave intact comes back as a caret rather than an inverted range.</summary>
    public static TextRange Shift(TextEdit edit, TextRange selection, AnchorBias caretBias)
    {
        if (selection.IsEmpty)
        {
            var caret = Shift(edit, selection.Start, caretBias);
            return TextRange.Caret(caret);
        }

        var start = Shift(edit, selection.Start, AnchorBias.After);
        var end = Shift(edit, selection.End, AnchorBias.Before);
        return end < start ? TextRange.Caret(start) : new TextRange(start, end);
    }

    /// <summary>The position just past the inserted text.</summary>
    public static TextPosition EndOfReplacement(TextEdit edit)
    {
        var start = edit.Range.Start;
        var replacement = edit.Replacement;

        var lines = 0;
        var lastBreakEnd = 0;
        for (var i = 0; i < replacement.Length; i++)
        {
            var c = replacement[i];
            if (c != '\n' && c != '\r') continue;
            if (c == '\r' && i + 1 < replacement.Length && replacement[i + 1] == '\n') i++;
            lines++;
            lastBreakEnd = i + 1;
        }

        if (lines == 0)
            return new TextPosition(start.Line, new RawColumn(start.Column.Value + replacement.Length));

        return TextPosition.At(start.Line.Value + lines, replacement.Length - lastBreakEnd);
    }
}
