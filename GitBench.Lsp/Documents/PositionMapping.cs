namespace GitBench.Lsp.Documents;


/// <summary>An index into the flattened row list the preview draws. Rows include headers and
/// separators, and a collapsed fold's body has no row at all, so this is not a line number.</summary>
public readonly record struct RowIndex(int Value);

/// <summary>A UTF-16 offset into a row's tab-expanded text — the space the painter, the hit-test
/// and the syntax spans count in.</summary>
public readonly record struct ScreenColumn(int Value);


/// <summary>A position on screen, in the coordinates the renderer and the mouse speak.</summary>
public readonly record struct ScreenPosition(RowIndex Row, ScreenColumn Column);


/// <summary>The part of one drawn row a file range covers.</summary>
public readonly record struct ScreenSpan(RowIndex Row, ScreenColumn From, ScreenColumn To);

/// <summary>An optional range, as a sum rather than a nullable field: LSP marks both a hover's
/// range and a location link's selection range optional, and "absent" is a case the reader has to
/// answer for rather than a null to trip over.</summary>
public abstract record OptionalRange
{
    private OptionalRange() { }

    public static readonly OptionalRange Absent = new NotGiven();

    public static OptionalRange Of(LspRange range) => new Present(range);

    public sealed record NotGiven : OptionalRange;

    public sealed record Present(LspRange Range) : OptionalRange;
}

/// <summary>
/// One line of a previewed file in both spaces at once: the raw file text and its tab-expanded
/// rendering, with the mapping between their column spaces. The same split
/// <c>Features/Diff/DiffLineText</c> already makes, minus the slicing: a server position is a
/// point, never a selection, so there is no tab edge to choose.
/// </summary>
public sealed record LineText
{
    public const int TabWidth = 4;

    private readonly bool _hasTabs;

    private LineText(string raw, string expanded, bool hasTabs)
    {
        Raw = raw;
        Expanded = expanded;
        _hasTabs = hasTabs;
    }

    public static LineText Of(string raw)
    {
        var hasTabs = raw.IndexOf('\t') >= 0;
        return new LineText(raw, hasTabs ? ExpandTabs(raw) : raw, hasTabs);
    }

    public string Raw { get; }
    public string Expanded { get; }

    /// <summary>The character just past the last one, where an end-of-line position sits.</summary>
    public LspCharacter End => new(Raw.Length);

    public ScreenColumn ToScreen(LspCharacter character)
    {
        var limit = Math.Clamp(character.Value, 0, Raw.Length);
        if (!_hasTabs) return new ScreenColumn(limit);

        var expanded = 0;
        for (var i = 0; i < limit; i++) expanded += Raw[i] == '\t' ? TabWidth : 1;
        return new ScreenColumn(expanded);
    }

    /// <summary>The character under a screen column. A column inside the run of spaces a tab
    /// expanded into resolves to the tab itself — it is one character, so there is nothing
    /// between its spaces to land on.</summary>
    public LspCharacter ToFile(ScreenColumn column)
    {
        var target = Math.Clamp(column.Value, 0, Expanded.Length);
        if (!_hasTabs) return new LspCharacter(target);

        var expanded = 0;
        for (var raw = 0; raw < Raw.Length; raw++)
        {
            var width = Raw[raw] == '\t' ? TabWidth : 1;
            if (target < expanded + width) return new LspCharacter(raw);
            expanded += width;
        }
        return new LspCharacter(Raw.Length);
    }

    private static string ExpandTabs(string raw)
    {
        var builder = new System.Text.StringBuilder(raw.Length + TabWidth);
        foreach (var c in raw)
        {
            if (c == '\t') builder.Append(' ', TabWidth);
            else builder.Append(c);
        }
        return builder.ToString();
    }
}

/// <summary>
/// A previewed file split into lines. Line terminators are not part of a line, so the same file
/// with CRLF and with LF has identical character offsets, and a final terminator does not add an
/// empty last line.
/// </summary>
public sealed class FileText
{
    private readonly LineText[] _lines;

    private FileText(LineText[] lines) => _lines = lines;

    public static FileText Of(string text)
    {
        var parts = text.Split('\n');
        var count = parts.Length > 1 && parts[^1].Length == 0 ? parts.Length - 1 : parts.Length;
        var lines = new LineText[count];
        for (var i = 0; i < count; i++)
        {
            var raw = parts[i];
            if (raw.EndsWith('\r')) raw = raw[..^1];
            lines[i] = LineText.Of(raw);
        }
        return new FileText(lines);
    }

    public int LineCount => _lines.Length;

    public bool Contains(LspLine line) => line.Value >= 0 && line.Value < _lines.Length;

    public LineText Line(LspLine line) => _lines[line.Value];
}

/// <summary>A row in the flattened list the preview draws.</summary>
public abstract record DocumentRow
{
    private DocumentRow() { }

    /// <summary>A header, a separator, or a "N lines hidden" marker: drawn, but not file text.</summary>
    public static readonly DocumentRow Chrome = new Decoration();

    public static DocumentRow For(LspLine line) => new Code(line);

    public sealed record Decoration : DocumentRow;

    public sealed record Code(LspLine Line) : DocumentRow;
}

/// <summary>Where a file position lands on screen. Total: a line inside a collapsed fold is drawn
/// nowhere but still has somewhere to scroll to, and a line the file does not have at all — a
/// result that outlived its version — is neither.</summary>
public abstract record ScreenLookup
{
    private ScreenLookup() { }

    public sealed record Shown(ScreenPosition Position) : ScreenLookup;

    /// <summary>The line is in the file but no row draws it. <paramref name="Anchor"/> is the
    /// nearest drawn row, which is where a jump scrolls and where a fold has to be opened.</summary>
    public sealed record Hidden(RowIndex Anchor) : ScreenLookup;

    public sealed record OffDocument : ScreenLookup;
}

/// <summary>Where a screen position lands in the file. A row that is chrome carries no file
/// position, and asking about one is not an error.</summary>
public abstract record FileLookup
{
    private FileLookup() { }

    public sealed record At(LspPosition Position) : FileLookup;

    public sealed record NoLine : FileLookup;
}

/// <summary>
/// The two coordinate spaces of a previewed file, and the total conversion between them. Rows the
/// renderer produced on one side, the file's own lines and UTF-16 offsets on the other; every
/// position a server sends comes in through here, and every position we send goes out through it.
/// </summary>
public sealed class RenderedDocument
{
    private readonly FileText _text;
    private readonly IReadOnlyList<DocumentRow> _rows;
    private readonly Dictionary<int, int> _rowOfLine;
    private readonly (int Line, int Row)[] _drawnLines;

    private RenderedDocument(
        FileText text,
        IReadOnlyList<DocumentRow> rows,
        Dictionary<int, int> rowOfLine,
        (int, int)[] drawnLines)
    {
        _text = text;
        _rows = rows;
        _rowOfLine = rowOfLine;
        _drawnLines = drawnLines;
    }

    /// <summary>Builds the mapping, rejecting a row list that cannot have come from this text: a
    /// row naming a line the file does not have, or a list that draws no file line at all. Both
    /// would leave a conversion with no honest answer to give, and both mean the rows and the text
    /// were built from different files.</summary>
    public static RenderedDocument Of(FileText text, IReadOnlyList<DocumentRow> rows)
    {
        var rowOfLine = new Dictionary<int, int>();
        var drawn = new List<(int, int)>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i] is not DocumentRow.Code code) continue;
            if (!text.Contains(code.Line))
                throw new ArgumentException(
                    $"row {i} draws line {code.Line.Value}, but the file has {text.LineCount} lines",
                    nameof(rows));
            if (rowOfLine.TryAdd(code.Line.Value, i)) drawn.Add((code.Line.Value, i));
        }
        if (drawn.Count == 0)
            throw new ArgumentException("a document that draws no file line is not a document", nameof(rows));

        drawn.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return new RenderedDocument(text, rows, rowOfLine, drawn.ToArray());
    }

    public int RowCount => _rows.Count;

    public int LineCount => _text.LineCount;

    public ScreenLookup ToScreen(LspPosition position)
    {
        if (!_text.Contains(position.Line)) return new ScreenLookup.OffDocument();
        if (!_rowOfLine.TryGetValue(position.Line.Value, out var row))
            return new ScreenLookup.Hidden(new RowIndex(AnchorFor(position.Line)));

        var column = _text.Line(position.Line).ToScreen(position.Character);
        return new ScreenLookup.Shown(new ScreenPosition(new RowIndex(row), column));
    }

    public FileLookup ToFile(ScreenPosition position)
    {
        if (position.Row.Value < 0 || position.Row.Value >= _rows.Count) return new FileLookup.NoLine();
        if (_rows[position.Row.Value] is not DocumentRow.Code code) return new FileLookup.NoLine();

        var character = _text.Line(code.Line).ToFile(position.Column);
        return new FileLookup.At(new LspPosition(code.Line, character));
    }

    /// <summary>The parts of the drawn rows a file range covers, in row order. Lines the document
    /// does not draw contribute nothing, so a diagnostic reaching into a collapsed fold underlines
    /// only what is on screen, and one entirely inside a fold underlines nothing.</summary>
    public IReadOnlyList<ScreenSpan> ToScreenSpans(LspRange range)
    {
        var (start, end) = Ordered(range);
        var spans = new List<ScreenSpan>();

        for (var line = start.Line.Value; line <= end.Line.Value; line++)
        {
            var at = new LspLine(line);
            if (!_text.Contains(at)) break;
            if (!_rowOfLine.TryGetValue(line, out var row)) continue;

            var text = _text.Line(at);
            var from = line == start.Line.Value ? start.Character : new LspCharacter(0);
            var to = line == end.Line.Value ? end.Character : text.End;

            // A range that ends at column 0 of a line covers the newline before it, not the line
            // itself; underlining there would mark a row the diagnostic never reached.
            if (line == end.Line.Value && line != start.Line.Value && end.Character.Value <= 0) continue;

            spans.Add(new ScreenSpan(new RowIndex(row), text.ToScreen(from), text.ToScreen(to)));
        }

        return spans;
    }

    private int AnchorFor(LspLine line)
    {
        var anchor = _drawnLines[0].Row;
        foreach (var (drawnLine, row) in _drawnLines)
        {
            if (drawnLine > line.Value) break;
            anchor = row;
        }
        return anchor;
    }

    private static (LspPosition Start, LspPosition End) Ordered(LspRange range)
    {
        var start = range.Start;
        var end = range.End;
        if (end.Line < start.Line || (end.Line == start.Line && end.Character.Value < start.Character.Value))
            return (end, start);
        return (start, end);
    }
}
