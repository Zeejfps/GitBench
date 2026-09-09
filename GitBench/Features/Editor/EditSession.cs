using System.Text;
using GitBench.Features.Diff;
using ZGF.Gui;

namespace GitBench.Features.Editor;

/// <summary>Everything a keystroke can mean, expressed once against a document: what each operation
/// changes, where the caret lands, and whether it joins the previous undo step. It does not own the
/// selection — each operation is handed one and returns the next — only the goal cell.</summary>
internal sealed class EditSession
{
    /// <summary>What an operation decided to do, before any of it has been applied. Edits are
    /// ordered so that applying them front to back leaves each later one still describing the text
    /// it named.</summary>
    private readonly record struct EditPlan(EditKind Kind, IReadOnlyList<TextEdit> Edits, AnchorBias Bias)
    {
        /// <summary>An operation with nothing to do: no step recorded, no redo path spent, the
        /// selection left exactly as it was.</summary>
        public static readonly EditPlan None = new(EditKind.None, Array.Empty<TextEdit>(), AnchorBias.After);

        public static EditPlan Of(EditKind kind, TextEdit edit) =>
            new(kind, new[] { edit }, AnchorBias.After);

        public static EditPlan Lines(IReadOnlyList<TextEdit> edits) =>
            new(EditKind.Boundary, edits, AnchorBias.Before);
    }

    private readonly TextDocument _document;
    private readonly EditJournal _journal;
    private CellColumn? _goal;

    /// <summary>Opens a session over a document. One session per open document, held for as long as
    /// the document is.</summary>
    /// <param name="applied">Told about each edit as it lands — see <see cref="EditJournal"/>.</param>
    public EditSession(TextDocument document, EditOptions options, Action<TextEdit>? applied = null)
    {
        _document = document;
        _journal = new EditJournal(document, applied);
        Options = options;
    }

    public TextDocument Document => _document;

    public EditJournal Journal => _journal;

    public EditOptions Options { get; }

    /// <summary>The column a run of vertical motion is aiming for, or null between runs.</summary>
    public CellColumn? GoalCell => _goal;

    /// <summary>Drops the column a run of vertical motion was aiming for. For carets placed from
    /// outside this type — a click, a drag, a jump to a search hit.</summary>
    public void ClearGoal() => _goal = null;

    // ---- Motion ----

    /// <summary>Moves the caret one cluster or one word. A plain arrow over a live selection
    /// collapses to its near end rather than stepping.</summary>
    public SelectionRange MoveBy(SelectionRange selection, TextUnit unit, MoveDirection direction, SelectionIntent intent)
    {
        _goal = null;
        var current = Clamp(selection);

        if (unit == TextUnit.Cluster && intent == SelectionIntent.Move && !current.IsEmpty)
            return SelectionRange.At(direction == MoveDirection.Backward ? current.Range.Start : current.Range.End);

        return current.To(Step(current.Caret, unit, direction), intent);
    }

    /// <summary>Moves the caret <paramref name="lines"/> lines, aiming for the goal cell remembered
    /// from the first move of the run. The one place that sets the goal cell.</summary>
    public SelectionRange MoveByLine(SelectionRange selection, int lines, SelectionIntent intent)
    {
        var current = Clamp(selection);
        var goal = _goal ?? CellAt(current.Caret);
        _goal = goal;

        var target = current.Caret.Line.Value + lines;
        if (target < 1)
            return current.To(TextPosition.At(1, 0), intent);
        if (target > _document.LineCount)
            return current.To(_document.End, intent);

        return current.To(PositionAtCell(new FileLine(target), goal), intent);
    }

    public SelectionRange MoveToLineEdge(SelectionRange selection, LineEdge edge, SelectionIntent intent)
    {
        _goal = null;
        var current = Clamp(selection);
        var text = _document.Line(current.Caret.Line);
        var column = edge == LineEdge.End ? text.Length : SmartStart(text, current.Caret.Column.Value);
        return current.To(current.Caret with { Column = new RawColumn(column) }, intent);
    }

    public SelectionRange MoveToDocumentEdge(SelectionRange selection, DocumentEdge edge, SelectionIntent intent)
    {
        _goal = null;
        var current = Clamp(selection);
        return current.To(edge == DocumentEdge.Start ? TextPosition.At(1, 0) : _document.End, intent);
    }

    // ---- Edits ----

    /// <summary>Enters typed text at the caret, replacing whatever is selected. One rune typed into
    /// a caret joins the run of typing before it; anything else stands as its own undo step.</summary>
    public SelectionRange Type(SelectionRange selection, string text)
    {
        _goal = null;
        var current = Clamp(selection);
        return Commit(current, PlanType(current, text));
    }

    /// <summary>Enters text from outside the keyboard — a paste, a drop, an assistant's suggestion —
    /// always as its own undo step, with line endings normalized and control characters dropped.</summary>
    public SelectionRange Paste(SelectionRange selection, string text)
    {
        _goal = null;
        var current = Clamp(selection);
        if (text.Length == 0) return current;

        var pasted = Sanitize(text);
        if (pasted.Length == 0) return current;
        return Commit(current, PlanReplace(current, pasted, EditKind.Boundary));
    }

    /// <summary>Deletes one unit, or the selection when there is one. At the start of a line a
    /// backward delete takes the line break instead.</summary>
    public SelectionRange Delete(SelectionRange selection, TextUnit unit, MoveDirection direction)
    {
        _goal = null;
        var current = Clamp(selection);
        return Commit(current, PlanDelete(current, unit, direction));
    }

    /// <summary>Breaks the line and carries down the indentation that precedes the caret.</summary>
    public SelectionRange InsertNewline(SelectionRange selection)
    {
        _goal = null;
        var current = Clamp(selection);
        return Commit(current, PlanNewline(current));
    }

    /// <summary>Indents. A selection touching more than one line moves those lines rather than
    /// replacing the text between its ends.</summary>
    public SelectionRange Indent(SelectionRange selection)
    {
        _goal = null;
        var current = Clamp(selection);
        return Commit(current, PlanIndent(current));
    }

    /// <summary>Outdents, always by whole lines. A line with nothing to remove contributes no edit.</summary>
    public SelectionRange Outdent(SelectionRange selection)
    {
        _goal = null;
        var current = Clamp(selection);
        return Commit(current, PlanOutdent(current));
    }

    /// <summary>Comments the selected lines, or uncomments them when every one of them is already
    /// commented. Blank lines neither vote nor change; a language with no line comment declines.</summary>
    public SelectionRange ToggleLineComment(SelectionRange selection)
    {
        _goal = null;
        var current = Clamp(selection);
        return Commit(current, PlanToggleLineComment(current));
    }

    /// <summary>Reverses the newest step, returning the selection it was made with, or null when
    /// there is nothing left to undo.</summary>
    public SelectionRange? Undo() => Restore(_journal.Undo());

    public SelectionRange? Redo() => Restore(_journal.Redo());

    // ---- Cells ----

    /// <summary>Which monospace cell a position sits in, tabs expanded and double-width glyphs
    /// counted twice.</summary>
    public CellColumn CellAt(TextPosition position)
    {
        var clamped = _document.Clamp(position);
        var text = DiffLineText.Of(_document.Line(clamped.Line));
        return new CellColumn(
            DiffText.CellsBefore(text.Expanded, text.ToExpanded(clamped.Column).Value));
    }

    /// <summary>The position a cell column names on a line, snapped out of the middle of a grapheme
    /// cluster or a tab's expansion.</summary>
    public TextPosition PositionAtCell(FileLine line, CellColumn cell)
    {
        var row = new FileLine(Math.Clamp(line.Value, 1, _document.LineCount));
        var text = DiffLineText.Of(_document.Line(row));
        return new TextPosition(
            row, new RawColumn(TextBoundaries.Snap(text.Raw, ColumnAtCell(text, cell.Value))));
    }

    // ---- Planning ----

    private static EditPlan PlanReplace(SelectionRange selection, string replacement, EditKind kind) =>
        EditPlan.Of(kind, new TextEdit(selection.Range, replacement));

    private static EditPlan PlanType(SelectionRange selection, string text)
    {
        if (text.Length == 0) return EditPlan.None;

        var kind = !IsSingleRune(text) ? EditKind.Boundary
            : selection.IsEmpty ? EditKind.Insert
            : EditKind.Replace;
        return PlanReplace(selection, text, kind);
    }

    private EditPlan PlanDelete(SelectionRange selection, TextUnit unit, MoveDirection direction)
    {
        if (!selection.IsEmpty)
            return PlanReplace(selection, string.Empty, EditKind.Boundary);

        var to = Step(selection.Caret, unit, direction);
        if (to == selection.Caret) return EditPlan.None;

        var range = direction == MoveDirection.Backward
            ? new TextRange(to, selection.Caret)
            : new TextRange(selection.Caret, to);
        var kind = unit == TextUnit.Cluster ? EditKind.Delete : EditKind.Boundary;
        return EditPlan.Of(kind, new TextEdit(range, string.Empty));
    }

    private EditPlan PlanNewline(SelectionRange selection)
    {
        var start = selection.Range.Start;
        var indent = LeadingIndent(_document.Line(start.Line), start.Column.Value);
        return PlanReplace(selection, Options.EolText + indent, EditKind.Boundary);
    }

    private EditPlan PlanIndent(SelectionRange selection)
    {
        var (start, end) = selection.Range;
        if (start.Line == end.Line)
            return PlanReplace(selection, Options.IndentAt(CellAt(start).Value), EditKind.Boundary);

        var (first, last) = LineSpan(selection);
        var edits = new List<TextEdit>();
        for (var line = last; line >= first; line--)
        {
            if (_document.Line(new FileLine(line)).Length == 0) continue;
            edits.Add(new TextEdit(TextRange.Caret(TextPosition.At(line, 0)), Options.IndentAt(0)));
        }

        return EditPlan.Lines(edits);
    }

    private EditPlan PlanOutdent(SelectionRange selection)
    {
        var (first, last) = LineSpan(selection);

        var edits = new List<TextEdit>();
        for (var line = last; line >= first; line--)
        {
            var width = OutdentWidth(_document.Line(new FileLine(line)));
            if (width == 0) continue;
            edits.Add(new TextEdit(
                new TextRange(TextPosition.At(line, 0), TextPosition.At(line, width)),
                string.Empty));
        }

        return EditPlan.Lines(edits);
    }

    private EditPlan PlanToggleLineComment(SelectionRange selection)
    {
        var token = Options.LineComment;
        if (token == null) return EditPlan.None;

        var (first, last) = LineSpan(selection);
        var commentedThroughout = true;
        var hasContent = false;
        for (var line = first; line <= last && commentedThroughout; line++)
        {
            var text = _document.Line(new FileLine(line));
            var indent = IndentWidth(text);
            if (indent == text.Length) continue;
            hasContent = true;
            commentedThroughout = text.AsSpan(indent).StartsWith(token);
        }

        if (!hasContent) return EditPlan.None;

        var edits = new List<TextEdit>();
        for (var line = last; line >= first; line--)
        {
            var text = _document.Line(new FileLine(line));
            var indent = IndentWidth(text);
            if (indent == text.Length) continue;

            if (commentedThroughout)
            {
                var width = token.Length;
                if (indent + width < text.Length && text[indent + width] == ' ') width++;
                edits.Add(new TextEdit(
                    new TextRange(TextPosition.At(line, indent), TextPosition.At(line, indent + width)),
                    string.Empty));
            }
            else if (!text.AsSpan(indent).StartsWith(token))
            {
                edits.Add(new TextEdit(TextRange.Caret(TextPosition.At(line, indent)), token + " "));
            }
        }

        return EditPlan.Lines(edits);
    }

    // ---- Applying ----

    /// <summary>The one place text changes. Where the selection lands is the journal's answer.</summary>
    private SelectionRange Commit(SelectionRange before, EditPlan plan) =>
        plan.Edits.Count == 0
            ? before
            : _journal.Apply(new EditTransaction(plan.Kind, plan.Edits, before, plan.Bias));

    private SelectionRange? Restore(TextRange? restored)
    {
        _goal = null;
        return restored == null ? null : SelectionRange.Of(restored.Value);
    }

    // ---- Positions ----

    private SelectionRange Clamp(SelectionRange selection) =>
        new(_document.Clamp(selection.Anchor), _document.Clamp(selection.Caret));

    private TextPosition Step(TextPosition caret, TextUnit unit, MoveDirection direction) =>
        unit == TextUnit.Cluster ? StepCluster(caret, direction) : StepWord(caret, direction);

    private TextPosition StepCluster(TextPosition caret, MoveDirection direction)
    {
        var text = _document.Line(caret.Line);
        if (direction == MoveDirection.Backward)
        {
            if (caret.Column.Value > 0)
                return caret with { Column = new RawColumn(TextBoundaries.Prev(text, caret.Column.Value)) };
            return caret.Line.Value > 1 ? LineEnd(new FileLine(caret.Line.Value - 1)) : caret;
        }

        if (caret.Column.Value < text.Length)
            return caret with { Column = new RawColumn(TextBoundaries.Next(text, caret.Column.Value)) };
        return caret.Line.Value < _document.LineCount ? TextPosition.At(caret.Line.Value + 1, 0) : caret;
    }

    private TextPosition StepWord(TextPosition caret, MoveDirection direction)
    {
        var text = _document.Line(caret.Line);
        if (direction == MoveDirection.Backward)
        {
            var column = TextBoundaries.PrevWord(text, caret.Column.Value);
            if (column < caret.Column.Value) return caret with { Column = new RawColumn(column) };
            return caret.Line.Value > 1 ? LineEnd(new FileLine(caret.Line.Value - 1)) : caret;
        }

        var next = TextBoundaries.NextWord(text, caret.Column.Value);
        if (next > caret.Column.Value) return caret with { Column = new RawColumn(next) };
        return caret.Line.Value < _document.LineCount ? TextPosition.At(caret.Line.Value + 1, 0) : caret;
    }

    private TextPosition LineEnd(FileLine line) => new(line, new RawColumn(_document.Line(line).Length));

    private static (int First, int Last) LineSpan(SelectionRange selection)
    {
        var (start, end) = selection.Range;
        var last = end.Line.Value;
        if (last > start.Line.Value && end.Column.Value == 0) last--;
        return (start.Line.Value, last);
    }

    private static int SmartStart(string text, int column)
    {
        var indent = IndentWidth(text);
        return column == indent ? 0 : indent;
    }

    private static int IndentWidth(string text)
    {
        var width = 0;
        while (width < text.Length && IsIndent(text[width])) width++;
        return width;
    }

    private static string LeadingIndent(string text, int limit)
    {
        var width = 0;
        var stop = Math.Min(limit, text.Length);
        while (width < stop && IsIndent(text[width])) width++;
        return text[..width];
    }

    private static int OutdentWidth(string text)
    {
        if (text.Length == 0) return 0;
        if (text[0] == '\t') return 1;

        var spaces = 0;
        while (spaces < text.Length && spaces < DiffOptions.TabWidth && text[spaces] == ' ') spaces++;
        return spaces;
    }

    private static bool IsIndent(char c) => c is ' ' or '\t';

    /// <summary>The raw column a cell names, taking the nearer edge of a tab the cell lands
    /// inside.</summary>
    private static int ColumnAtCell(DiffLineText text, int cell)
    {
        if (cell <= 0) return 0;

        var target = new ExpandedColumn(DiffText.CharIndexAtCell(text.Expanded, cell));
        var before = text.ToRaw(target, TabEdge.Before);
        var after = text.ToRaw(target, TabEdge.After);
        if (before == after) return before.Value;

        var low = text.ToExpanded(before).Value;
        var high = text.ToExpanded(after).Value;
        return target.Value - low >= (high - low + 1) / 2 ? after.Value : before.Value;
    }

    private string Sanitize(string text)
    {
        var eol = Options.EolText;
        var normalized = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                normalized.Append(eol);
                continue;
            }

            if (c != '\t' && char.IsControl(c)) continue;
            normalized.Append(c);
        }

        return normalized.ToString();
    }

    private static bool IsSingleRune(string text) =>
        text.Length == 1
        || (text.Length == 2 && char.IsHighSurrogate(text[0]) && char.IsLowSurrogate(text[1]));
}
