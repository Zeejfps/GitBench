using System.Collections;
using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Theming;

namespace GitBench.Features.Editor;

/// <summary>A <see cref="TextDocument"/> seen as the row stream the diff painter draws, materialized
/// per row and re-measured per edit rather than flattened per render.</summary>
internal sealed class EditorRowSet : IDiffRowSource, IAnchoredRows
{
    private readonly TextDocument _document;
    private readonly ILocalizationService _loc;
    private readonly DocumentLines _documentLines;
    private readonly List<Line> _lines = new();
    private readonly CellWidths _widths = new();
    private readonly RowList _rows;
    private readonly DiffRow.Banner? _truncation;
    private readonly OwningThread _thread = OwningThread.Current();

    private DiffHighlight? _highlight;
    private FileOutline? _outline;
    private FoldState? _folds;
    private bool _usageLensRows;
    private int _revision;

    private FoldPlan _plan = FoldPlan.Nothing;
    // Null while nothing folds and no usages row exists: a row index is then a line index.
    private int[]? _rowOfLine;
    private int[]? _lineOfRow;
    private int _hiddenLines;
    private int _lensRows;
    private int _ghostRows;
    private GhostLines? _ghost;

    /// <param name="truncated">Whether the loader stopped short of the end of the file, which closes
    /// the stream with a truncation banner.</param>
    public EditorRowSet(
        TextDocument document,
        ILocalizationService loc,
        DiffHighlight? highlight = null,
        bool truncated = false)
    {
        _document = document;
        _loc = loc;
        _highlight = highlight;
        _revision = document.Revision;
        _rows = new RowList(this);
        _documentLines = new DocumentLines(document);

        for (var i = 0; i < document.LineCount; i++)
        {
            var cells = Measure(i);
            _lines.Add(new Line(cells, null));
            _widths.Add(cells);
        }

        if (!truncated) return;

        _truncation = new DiffRow.Banner(loc.Strings.Value.DiffFileTruncated(ContentLineCount));
        _widths.Add(DiffText.VisualCells(_truncation.Text));
    }

    /// <summary>Told which declaration the projection had to open to uncover a caret.</summary>
    public Action<string>? FoldExpanded { get; set; }

    /// <summary>The rows, materialized as they are asked for. <see cref="IReadOnlyList{T}.Count"/>
    /// is exact and costs nothing.</summary>
    public IReadOnlyList<DiffRow> Rows => _rows;

    /// <summary>The declarations the last parse found, shifted along with the edits made since.</summary>
    public FileOutline? Outline => _outline;

    /// <summary>The widest row in monospace cells.</summary>
    public int MaxRowCells
    {
        get
        {
            AssertThread();
            return _widths.Max;
        }
    }

    /// <summary>Max line-number digit count, for gutter width sizing.</summary>
    public int GutterDigits => FullFileRow.GutterDigits(_lines.Count);

    /// <summary>Always a single (new-side) gutter: a document has one set of line numbers.</summary>
    public bool SingleGutter => true;

    /// <summary>Reserved as soon as the reader has a fold set for this file, not when something in
    /// it is actually collapsed.</summary>
    public bool FoldColumn => _folds is not null;

    /// <summary>Never reserved: nothing in a document being edited is marked as an addition.</summary>
    public bool GlyphColumn => false;

    public Func<RowIndex, string?>? HiddenText => FoldColumn ? HiddenAfter : null;

    /// <summary>A document is not grouped into hunks.</summary>
    public IDiffHunkRows? Hunks => null;

    /// <summary>How many times a line has been measured for its width, for the test that holds the
    /// cost model honest.</summary>
    public int LineMeasurements { get; private set; }

    /// <summary>Whether declarations carry a usages row. Off unless the surface asks.</summary>
    public bool UsageLensRows
    {
        get => _usageLensRows;
        set
        {
            AssertThread();
            if (_usageLensRows == value) return;
            _usageLensRows = value;
            Replan();
        }
    }

    /// <summary>Suggested lines drawn after a line of the file, which moves with the edits above
    /// it; null for none.</summary>
    public GhostLines? Ghost => _ghost;

    /// <summary>Draws suggested lines after a line of the file, or none. They are rows without a
    /// line: the caret, the selection and search never land on them.</summary>
    public void SetGhost(GhostLines? ghost)
    {
        AssertThread();
        if (Equals(_ghost, ghost)) return;
        Reshaping?.Invoke();
        _ghost = ghost;
        Replan();
        Reshaped?.Invoke();
    }

    /// <summary>The file line a row stands for, or null for a usages row, a suggested line, the
    /// truncation banner and a row index this stream does not have.</summary>
    public FileLine? NewLineAt(RowIndex row)
    {
        AssertThread();
        if (row.Value < 0 || row.Value >= RowCount) return null;
        if (_truncation != null && row.Value == RowCount - 1) return null;
        var line = LineOfRow(row.Value);
        return RowOfLine(line) == row.Value ? new FileLine(line) : null;
    }

    /// <summary>The row standing for a file line, or null when the document has no such line or a
    /// collapsed declaration swallowed it.</summary>
    public RowIndex? RowForNewLine(FileLine line)
    {
        AssertThread();
        if (line.Value < 1 || line.Value > _lines.Count) return null;
        var row = RowOfLine(line.Value);
        return row < 0 ? null : new RowIndex(row);
    }

    /// <summary>Where to scroll for a file line: its own row, or the nearest visible row above it.
    /// Null when there is no row to land on.</summary>
    public RowIndex? RowNearestNewLine(FileLine line)
    {
        AssertThread();
        if (_lines.Count == 0 || line.Value < 1) return null;

        var index = Math.Min(line.Value, _lines.Count) - 1;
        while (index >= 0 && RowOfLine(index + 1) < 0) index--;
        return index < 0 ? null : new RowIndex(RowOfLine(index + 1));
    }

    public DiffRowAnchor? AnchorAt(RowIndex row)
    {
        AssertThread();
        return DiffRowAnchors.AnchorAt(this, row);
    }

    public RowIndex? RowAt(DiffRowAnchor anchor)
    {
        AssertThread();
        return DiffRowAnchors.RowAt(this, anchor);
    }

    int IAnchoredRows.RowCount => RowCount;

    DiffRowKey? IAnchoredRows.KeyAt(int row) => KeyAt(row);

    RowIndex? IAnchoredRows.RowFor(DiffRowKey key) =>
        key.Side == DiffLineSide.New ? RowForNewLine(key.Line) : null;

    /// <summary>How many times the projection has had to rebuild itself from the document rather
    /// than follow an edit into it, for the test that holds the contract honest.</summary>
    public int Resynchronizations { get; private set; }

    /// <summary>Re-projects the rows one edit touched, and only those. Takes the edit
    /// <see cref="TextDocument.Apply"/> handed back — the one that would undo it. Runs mid-edit, so
    /// it must never throw: a call it cannot follow rebuilds instead.</summary>
    public void Reproject(TextEdit undo)
    {
        AssertThread();
        if (_document.Revision == _revision) return;

        if (_document.Revision == _revision + 1 && Follows(undo) is { } landed)
        {
            ShiftHighlight(undo, landed.First, landed.LastOld, landed.LastNew);
            Replace(landed.First, landed.LastOld, landed.LastNew);
            _revision = _document.Revision;
            return;
        }

        Rebuild();
    }

    private (int First, int LastOld, int LastNew)? Follows(TextEdit undo)
    {
        var first = undo.Range.Start.Line.Value - 1;
        var lastNew = undo.Range.End.Line.Value - 1;
        var lastOld = lastNew - (_document.LineCount - _lines.Count);
        if (first < 0 || first > lastOld || lastOld >= _lines.Count || lastNew >= _document.LineCount)
            return null;
        return BreakCount(undo.Replacement) == lastOld - first ? (first, lastOld, lastNew) : null;
    }

    private void Rebuild()
    {
        Resynchronizations++;
        _lines.Clear();
        for (var i = 0; i < _document.LineCount; i++) _lines.Add(new Line(Measure(i), null));
        _revision = _document.Revision;
        _outline = null;
        Replan();
    }

    /// <summary>
    /// Raised either side of a reshape nobody holding a row asked for: a fresh parse arriving from
    /// the worker with different declarations, and with them different usages rows. Whoever keeps
    /// positions as rows names them on the first and finds them again on the second — a caret left
    /// pointing at a row index would otherwise sit on whatever row now has that index.
    /// </summary>
    public event Action? Reshaping;

    /// <inheritdoc cref="Reshaping"/>
    public event Action? Reshaped;

    /// <summary>Re-colors and re-folds the projection from a fresh parse, or refuses one that
    /// describes an earlier revision. Returns whether it was applied.</summary>
    public bool SetAnnotations(Revised<DiffAnnotations> annotations)
    {
        AssertThread();
        if (!annotations.TryReadFor(_document, out var value)) return false;

        Reshaping?.Invoke();
        _highlight = value.Highlight;
        _outline = value.NewSide;
        Replan();
        Reshaped?.Invoke();
        return true;
    }

    /// <summary>Replaces the set of declarations the reader has folded shut.</summary>
    public void SetFolds(FoldState? folds)
    {
        AssertThread();
        if (Equals(_folds, folds)) return;
        _folds = folds;
        Replan();
    }

    /// <summary>Opens whatever collapsed declaration hides a line, so a caret steered into one has a
    /// row to sit on. Returns whether anything opened.</summary>
    public bool Reveal(FileLine line)
    {
        AssertThread();
        if (_folds is not { } folds) return false;
        if (_plan.CollapsedOver(line.Value) is not { } path) return false;

        _folds = folds.Expanded(path);
        Replan();
        FoldExpanded?.Invoke(path);
        return true;
    }

    private int ContentLineCount =>
        _document.Length == 0 ? 0 : _document.LineCount - (_document.EndsWithNewline ? 1 : 0);

    private int RowCount =>
        _lines.Count - _hiddenLines + _lensRows + _ghostRows + (_truncation != null ? 1 : 0);

    private DiffRowKey? KeyAt(int row) =>
        NewLineAt(new RowIndex(row)) is { } line ? DiffRowKey.NewSide(line) : null;

    private int RowOfLine(int line) => _rowOfLine is null ? line - 1 : _rowOfLine[line - 1];

    private int LineOfRow(int row) => _lineOfRow is null ? row + 1 : _lineOfRow[row];

    private string? HiddenAfter(RowIndex row)
    {
        AssertThread();
        return NewLineAt(row) is { } line ? _plan.SwallowedAt(line.Value) : null;
    }

    private DiffRow MaterializedRow(int index)
    {
        AssertThread();
        if (index < 0 || index >= RowCount)
            throw new ArgumentOutOfRangeException(nameof(index), index, "No such row in this document.");
        if (_truncation is { } banner && index == RowCount - 1) return banner;

        var line = LineOfRow(index);
        var own = RowOfLine(line);
        if (index > own && _ghost is { } ghost)
            return new DiffRow.Ghost(DiffText.ExpandTabs(ghost.Lines[index - own - 1]), ghost.Emphasis?[index - own - 1]);
        if (own != index)
            return _plan.LensAt(line)
                ?? throw new InvalidOperationException(
                    $"Row {index} indexes line {line}, which is not where that line is drawn and " +
                    "carries no usages row either.");

        var cached = _lines[line - 1].Row;
        if (cached is not null && cached.NewNumber.Line?.Value == line) return cached;

        var row = Materialize(line);
        _lines[line - 1] = _lines[line - 1] with { Row = row };
        return row;
    }

    private DiffRow.Line Materialize(int lineNumber) =>
        FullFileRow.Line(
            DiffLineKind.Context, lineNumber, _document.Line(new FileLine(lineNumber)), _highlight, null, _plan.MarkAt(lineNumber));

    private int Measure(int index)
    {
        LineMeasurements++;
        return DiffText.VisualCells(DiffText.ExpandTabs(_document.Line(new FileLine(index + 1))));
    }

    private int Contribution(int index) =>
        _lines[index].Cells + FullFileRow.ChipCells(_plan.MarkAt(index + 1));

    /// <summary>
    /// Carries the colors of the last parse across an edit until the next parse replaces them. Lines
    /// above the edit keep theirs and lines below move with it; inside it, a line that reads exactly
    /// as some line of the replaced text did takes that line's colors, so a line pushed down by a
    /// newline typed before it stays colored. A single line edited in place keeps its own, which are
    /// close enough for the moment the parse takes. Anything else draws plain until the parse lands.
    /// </summary>
    /// <remarks>
    /// Without this the table stays indexed by the old line numbers, and every line below an
    /// inserted one is drawn in the colors of the line that used to stand at its number.
    /// </remarks>
    private void ShiftHighlight(TextEdit undo, int first, int lastOld, int lastNew)
    {
        if (_highlight is not { } highlight) return;

        var replaced = ReplacedLines(undo);
        var used = new bool[replaced.Length];
        var shifted = new List<IReadOnlyList<TokenSpan>>(_document.LineCount);

        for (var i = 0; i < first; i++) shifted.Add(highlight.NewSide(i + 1));

        for (var i = first; i <= lastNew; i++)
        {
            var text = _document.Line(new FileLine(i + 1));
            var match = -1;
            for (var k = 0; k < replaced.Length && match < 0; k++)
                if (!used[k] && replaced[k] == text) match = k;
            if (match < 0 && replaced.Length == 1 && lastNew == first) match = 0;
            if (match >= 0) used[match] = true;
            shifted.Add(match >= 0 ? highlight.NewSide(first + match + 1) : []);
        }

        var delta = lastNew - lastOld;
        for (var i = lastNew + 1; i < _document.LineCount; i++) shifted.Add(highlight.NewSide(i - delta + 1));

        _highlight = highlight.WithNewSide(shifted);
    }

    /// <summary>The lines the edit replaced, read back out of the edit that would undo it: the text
    /// around the change on its first and last lines, with what it removed between.</summary>
    private string[] ReplacedLines(TextEdit undo)
    {
        var (start, end) = (undo.Range.Start, undo.Range.End);
        var firstLine = _document.Line(start.Line);
        var lastLine = _document.Line(end.Line);
        var before = firstLine[..Math.Min(start.Column.Value, firstLine.Length)];
        var after = lastLine[Math.Min(end.Column.Value, lastLine.Length)..];

        var lines = (before + undo.Replacement + after).Split('\n');
        for (var i = 0; i < lines.Length; i++) lines[i] = lines[i].TrimEnd('\r');
        return lines;
    }

    private void Replace(int first, int lastOld, int lastNew)
    {
        var patch = (_plan.IsEmpty && _ghost is null) || lastOld == lastNew;

        if (patch)
            for (var i = first; i <= lastOld; i++)
                if (!_widths.Remove(Contribution(i)))
                {
                    patch = false;
                    Resynchronizations++;
                    break;
                }

        var before = lastOld - first + 1;
        var after = lastNew - first + 1;
        if (after < before) _lines.RemoveRange(first + after, before - after);
        else if (after > before) _lines.InsertRange(first + before, new Line[after - before]);

        for (var i = first; i <= lastNew; i++) _lines[i] = new Line(Measure(i), null);

        if (_outline is { } outline && lastOld != lastNew)
            _outline = Shifted(outline, lastOld + 1, lastNew - lastOld);
        if (_ghost is { } ghost && lastOld != lastNew)
            _ghost = ghost with { After = new FileLine(Math.Clamp(Move(ghost.After.Value, lastOld + 1, lastNew - lastOld), 1, Math.Max(1, _lines.Count))) };

        if (!patch)
        {
            Replan();
            return;
        }

        for (var i = first; i <= lastNew; i++) _widths.Add(Contribution(i));
    }

    /// <summary>Everything derived from the outline and the fold set at once: which lines are
    /// hidden, which carry a mark, where the usages rows go, and the row index and widths that
    /// follow. Derived, never patched.</summary>
    private void Replan()
    {
        _plan = FoldPlan.Build(_outline, _folds, _usageLensRows, _documentLines);
        Reindex();
    }

    private static FileOutline Shifted(FileOutline outline, int after, int delta) =>
        new(Shifted(outline.Roots, after, delta), Shifted(outline.Regions, after, delta));

    private static IReadOnlyList<FoldRegion> Shifted(IReadOnlyList<FoldRegion> regions, int after, int delta)
    {
        var shifted = new FoldRegion[regions.Count];
        for (var i = 0; i < regions.Count; i++)
        {
            var region = regions[i];
            shifted[i] = region with
            {
                StartLine = Move(region.StartLine, after, delta),
                EndLine = Move(region.EndLine, after, delta),
            };
        }
        return shifted;
    }

    private static IReadOnlyList<OutlineNode> Shifted(
        IReadOnlyList<OutlineNode> nodes, int after, int delta)
    {
        var shifted = new OutlineNode[nodes.Count];
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            shifted[i] = node with
            {
                StartLine = Move(node.StartLine, after, delta),
                EndLine = Move(node.EndLine, after, delta),
                SignatureEndLine = Move(node.SignatureEndLine, after, delta),
                NameLine = new FileLine(Move(node.NameLine.Value, after, delta)),
                Children = Shifted(node.Children, after, delta),
            };
        }
        return shifted;
    }

    private static int Move(int line, int after, int delta) =>
        line > after ? Math.Max(after, line + delta) : line;

    private void Reindex()
    {
        _widths.Clear();
        if (_truncation is { } banner) _widths.Add(DiffText.VisualCells(banner.Text));
        _hiddenLines = 0;
        _lensRows = 0;
        _ghostRows = 0;

        if (_plan.IsEmpty && _ghost is null)
        {
            _rowOfLine = null;
            _lineOfRow = null;
            for (var i = 0; i < _lines.Count; i++)
            {
                _lines[i] = _lines[i] with { Row = null };
                _widths.Add(_lines[i].Cells);
            }
            return;
        }

        var rowOfLine = new int[_lines.Count];
        var lineOfRow = new List<int>(_lines.Count);
        for (var i = 0; i < _lines.Count; i++)
        {
            _lines[i] = _lines[i] with { Row = null };
            var line = i + 1;
            if (_plan.IsHidden(line))
            {
                rowOfLine[i] = -1;
                _hiddenLines++;
                continue;
            }

            if (_plan.LensAt(line) is { } lens)
            {
                lineOfRow.Add(line);
                _lensRows++;
                _widths.Add(FullFileRow.LensCells(lens));
            }

            rowOfLine[i] = lineOfRow.Count;
            lineOfRow.Add(line);
            _widths.Add(Contribution(i));

            if (_ghost is not { } ghost || ghost.After.Value != line) continue;
            foreach (var text in ghost.Lines)
            {
                lineOfRow.Add(line);
                _ghostRows++;
                _widths.Add(DiffText.VisualCells(DiffText.ExpandTabs(text)));
            }
        }

        _rowOfLine = rowOfLine;
        _lineOfRow = lineOfRow.ToArray();
    }

    private static int BreakCount(string text)
    {
        var breaks = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\n' && c != '\r') continue;
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            breaks++;
        }
        return breaks;
    }

    private void AssertThread() => _thread.Assert("An editable projection");

    /// <summary>One line's width in cells beside the row built from it, if anything has asked for
    /// one yet.</summary>
    private readonly record struct Line(int Cells, DiffRow.Line? Row);

    /// <summary>The document's lines as the fold planner reads a file's.</summary>
    private sealed class DocumentLines(TextDocument document) : IReadOnlyList<string>
    {
        public int Count => document.LineCount;

        public string this[int index] => document.Line(new FileLine(index + 1));

        public IEnumerator<string> GetEnumerator()
        {
            for (var i = 0; i < Count; i++) yield return this[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Every row's width at once, as a multiset keyed by width, so the widest is a lookup
    /// rather than a scan and a shrinking row brings the maximum back down.</summary>
    private sealed class CellWidths
    {
        private readonly Dictionary<int, int> _rows = new();
        private readonly SortedSet<int> _distinct = new();

        public int Max => _distinct.Count == 0 ? 0 : _distinct.Max;

        public void Add(int cells)
        {
            _rows.TryGetValue(cells, out var rows);
            _rows[cells] = rows + 1;
            if (rows == 0) _distinct.Add(cells);
        }

        /// <summary>Takes one row's width back out. False where that width was never in here.</summary>
        public bool Remove(int cells)
        {
            if (!_rows.TryGetValue(cells, out var rows)) return false;

            if (rows > 1)
            {
                _rows[cells] = rows - 1;
                return true;
            }

            _rows.Remove(cells);
            _distinct.Remove(cells);
            return true;
        }

        public void Clear()
        {
            _rows.Clear();
            _distinct.Clear();
        }
    }

    /// <summary>The row stream as a list, materialized per index.</summary>
    private sealed class RowList(EditorRowSet owner) : IReadOnlyList<DiffRow>
    {
        public int Count
        {
            get
            {
                owner.AssertThread();
                return owner.RowCount;
            }
        }

        public DiffRow this[int index] => owner.MaterializedRow(index);

        public IEnumerator<DiffRow> GetEnumerator()
        {
            for (var i = 0; i < Count; i++) yield return owner.MaterializedRow(i);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

/// <summary>Lines suggested to the reader, drawn after a line of the file, with the characters that
/// differ from the lines they replace, if any. Compared by content, so setting the same suggestion
/// again changes nothing.</summary>
internal sealed record GhostLines(FileLine After, IReadOnlyList<string> Lines, IReadOnlyList<IReadOnlyList<CharRange>?>? Emphasis = null)
{
    public bool Equals(GhostLines? other) =>
        other is not null && After == other.After && Lines.SequenceEqual(other.Lines) && SameEmphasis(Emphasis, other.Emphasis);

    private static bool SameEmphasis(IReadOnlyList<IReadOnlyList<CharRange>?>? a, IReadOnlyList<IReadOnlyList<CharRange>?>? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i] is null || b[i] is null)
            {
                if (a[i] is not null || b[i] is not null) return false;
                continue;
            }

            if (!a[i]!.SequenceEqual(b[i]!)) return false;
        }

        return true;
    }

    public override int GetHashCode() => HashCode.Combine(After, Lines.Count);
}
