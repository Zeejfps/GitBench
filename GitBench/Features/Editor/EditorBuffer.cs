using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using ZGF.Gui;

namespace GitBench.Features.Editor;

/// <summary>Which version of a document a value describes. Obtainable only from a document.</summary>
internal readonly record struct DocumentRevision
{
    private readonly int _value;

    private DocumentRevision(int value) => _value = value;

    public static DocumentRevision Of(TextDocument document) => new(document.Revision);

    public bool Describes(TextDocument document) => _value == document.Revision;
}

/// <summary>A value that describes one revision of a document, and can be read out only against that
/// document.</summary>
internal readonly struct Revised<T>
{
    private readonly T _value;
    private readonly DocumentRevision _at;
    private readonly bool _stamped;

    public Revised(DocumentRevision at, T value)
    {
        _at = at;
        _value = value;
        _stamped = true;
    }

    /// <summary>The value, where it still describes this document.</summary>
    public bool TryReadFor(TextDocument document, out T value)
    {
        value = _value;
        return _stamped && _at.Describes(document);
    }
}

/// <summary>One change as it landed: the edit that puts the document back, the text the change put
/// in, and the revision the document reached by taking it.</summary>
/// <remarks>
/// <see cref="Inserted"/> is not recoverable from <see cref="Inverse"/> — the inverse names where
/// the new text sits and what the old text was, never what the new text says — so it is read out
/// while the document is still on the thread that owns it.
/// </remarks>
internal readonly record struct DocumentEdit(DocumentRevision At, TextEdit Inverse, string Inserted);

/// <summary>One file open for editing: its text, its edit settings, the rows the painter draws it
/// as, and the only conversion between <see cref="DiffTextPos"/> and <see cref="TextPosition"/>.</summary>
internal sealed class EditorBuffer
{
    private readonly TextDocument _document;
    private readonly EditorRowSet _rows;

    private EditorBuffer(
        string path,
        TextDocument document,
        FileEncoding encoding,
        EditOptions options,
        EditorRowSet rows)
    {
        Path = path;
        Encoding = encoding;
        _document = document;
        _rows = rows;
        Opened = DocumentRevision.Of(document);
        Read = Opened;
        Session = new EditSession(document, options, Landed);
    }

    /// <summary>Raised as each edit lands, once per edit, on the thread that made it. The projection
    /// has already followed the edit by the time this runs, so a listener sees a document and a row
    /// stream that agree.</summary>
    public event Action<DocumentEdit>? Edited;

    // The one consumer EditSession has room for, split here rather than in the journal: the
    // projection has to follow every edit in order and mid-edit, and a listener that is merely
    // interested must not be able to come between the two.
    private void Landed(TextEdit inverse)
    {
        _rows.Reproject(inverse);
        if (Edited is not { } listeners) return;
        listeners(new DocumentEdit(DocumentRevision.Of(_document), inverse, _document.Slice(inverse.Range)));
    }

    /// <summary>Opens a file the preview pane has already read, or returns null when the decode was
    /// not reversible.</summary>
    public static EditorBuffer? TryOpen(
        string path,
        FileText text,
        FileWriteBack writeBack,
        DiffHighlight? highlight,
        ILocalizationService loc)
    {
        if (writeBack is not FileWriteBack.Reversible(var encoding)) return null;

        var document = TextDocument.FromText(text.Text);
        var options = EditOptions.For(path, encoding.LineEnding, text);
        return new EditorBuffer(
            path, document, encoding, options, new EditorRowSet(document, loc, highlight));
    }

    public string Path { get; }

    /// <summary>What the file took to read and what a save has to put back.</summary>
    public FileEncoding Encoding { get; }

    /// <summary>The text a save serializes.</summary>
    public TextDocument Document => _document;

    /// <summary>The revision the file was read at, which is what everything computed from that read
    /// describes.</summary>
    public DocumentRevision Opened { get; }

    /// <summary>The revision the file on disk, as the application last read it, describes. Moves
    /// when a re-read finds the document already saying what the file does.</summary>
    public DocumentRevision Read { get; private set; }

    /// <summary>
    /// Whether the file as the application last read it still says what this document says, which
    /// is what decides whether anything computed from that read — a parse, a search, a server's
    /// diagnostics — still describes what is on screen.
    /// </summary>
    /// <remarks>
    /// Measured against <see cref="Read"/> rather than <see cref="Opened"/>: a save is written and
    /// read back, and the read that comes back describes the document again. Against the opening
    /// revision this would go false on the first keystroke and never come back, so everything
    /// gated on it would stay hidden for as long as the file was open.
    /// </remarks>
    public bool ReadIsCurrent => Read.Describes(_document);

    /// <summary>The rows, for the surface to draw and to size itself from.</summary>
    public EditorRowSet Rows => _rows;

    /// <summary>Colors and folds from a parse of the file as it was read off disk, which describes
    /// this document only while nothing has been typed on top of that read. Returns whether it was
    /// applied.</summary>
    public bool ApplyRead(DiffAnnotations annotations) =>
        _rows.SetAnnotations(new Revised<DiffAnnotations>(Read, annotations));

    /// <summary>Records that the file has been read again and found to say exactly what this
    /// document already does, so that read's parse describes this revision.</summary>
    public void Reread() => Read = DocumentRevision.Of(_document);

    /// <summary>Everything a keystroke can mean, and the goal cell a run of vertical motion aims
    /// at.</summary>
    public EditSession Session { get; }

    /// <summary>The caret and selection as the document counts them, read out of the model the
    /// painter draws from. An inactive model reads as a caret at the top of the file.</summary>
    public SelectionRange SelectionOf(DiffSelectionModel selection) =>
        selection.IsActive
            ? new SelectionRange(PositionOf(selection.Anchor), PositionOf(selection.Focus))
            : SelectionRange.At(TextPosition.At(1, 0));

    /// <summary>Puts a selection back into the model the painter reads, opening whatever collapsed
    /// declaration hid the caret. Returns whether anything moved.</summary>
    public bool Write(DiffSelectionModel selection, SelectionRange range, object? scope)
    {
        var opened = _rows.Reveal(_document.Clamp(range.Caret).Line);
        return selection.SetRange(scope, PosOf(range.Anchor), PosOf(range.Caret)) || opened;
    }

    /// <summary>The nearest place a caret may actually sit: never inside a tab's expansion, never
    /// inside a grapheme cluster.</summary>
    public DiffTextPos Snap(DiffTextPos pos)
    {
        if (TextAt(pos.Row) is not { } text) return pos;
        var column = Math.Clamp(pos.Char.Value, 0, text.Expanded.Length);

        var before = text.ToRaw(new ExpandedColumn(column), TabEdge.Before);
        var after = text.ToRaw(new ExpandedColumn(column), TabEdge.After);
        var raw = before;
        if (before != after)
        {
            var low = text.ToExpanded(before).Value;
            var high = text.ToExpanded(after).Value;
            if (column - low >= (high - low + 1) / 2) raw = after;
        }

        return new DiffTextPos(pos.Row, text.ToExpanded(
            new RawColumn(TextBoundaries.Snap(text.Raw, raw.Value))));
    }

    /// <summary>Where a row position sits in the document. A row that stands for no line reads as
    /// the end of the document.</summary>
    public TextPosition PositionOf(DiffTextPos pos)
    {
        if (_rows.NewLineAt(pos.Row) is not { } line) return _document.End;
        if (TextAt(pos.Row) is not { } text) return new TextPosition(line, default);
        return new TextPosition(line, text.ToRaw(pos.Char, TabEdge.Before));
    }

    /// <summary>Where a document position sits in the rows. A line a collapsed declaration swallowed
    /// reads as the row that stands for it.</summary>
    public DiffTextPos PosOf(TextPosition position)
    {
        var clamped = _document.Clamp(position);
        if (_rows.RowForNewLine(clamped.Line) is not { } row)
            return _rows.RowNearestNewLine(clamped.Line) is { } nearest
                ? new DiffTextPos(nearest, default)
                : default;

        return TextAt(row) is { } text
            ? new DiffTextPos(row, text.ToExpanded(clamped.Column))
            : new DiffTextPos(row, default);
    }

    private DiffLineText? TextAt(RowIndex row) =>
        row.Value >= 0 && row.Value < _rows.Rows.Count && _rows.Rows[row.Value] is DiffRow.Line line
            ? line.Text
            : null;
}
