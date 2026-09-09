using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;

namespace GitBench.Features.Editor;

/// <summary>
/// One open document as the parser sees it: the bytes, the trees kept over them, and the colouring
/// and outline those trees produce.
/// </summary>
/// <remarks>
/// <para>
/// Two trees, not one. The highlighter and the outline extractor each own their grammar's compiled
/// query and their own pool of parsers, and one tree feeding both would mean prising those apart;
/// the second parse costs microseconds incrementally, which is less than that refactor is worth.
/// </para>
/// <para>
/// Single-threaded, and holds no reference to a document, a buffer or a view. One of these belongs
/// to one file on one worker, which is what lets it own native trees at all.
/// </para>
/// </remarks>
internal sealed class DocumentParse : IDisposable
{
    private readonly TreeSitterSyntaxHighlighter _highlighter;
    private readonly TreeSitterSymbolExtractor _extractor;
    private readonly string _languageId;
    private readonly CodeLanguage? _outlineLanguage;
    private readonly MaintainedTree? _highlightTree;
    private readonly MaintainedTree? _outlineTree;

    private ParseBuffer _buffer;

    public DocumentParse(
        TreeSitterSyntaxHighlighter highlighter,
        TreeSitterSymbolExtractor extractor,
        string languageId,
        CodeLanguage? outlineLanguage,
        string text)
    {
        _highlighter = highlighter;
        _extractor = extractor;
        _languageId = languageId;
        _outlineLanguage = outlineLanguage;
        _buffer = new ParseBuffer(text);
        _highlightTree = highlighter.Track(languageId);
        _outlineTree = outlineLanguage is { } language ? extractor.Track(language) : null;
    }

    /// <summary>Whether this can say anything at all about the file. False for a language neither
    /// engine holds a query for, which is the caller's cue not to keep one of these.</summary>
    public bool Tracks => _highlightTree is not null || _outlineTree is not null;

    /// <summary>How many times an edit produced a tree that did not describe the buffer and the
    /// buffer had to be parsed whole instead. Zero is the assertion the equivalence tests want:
    /// equality alone would also hold if every edit fell back.</summary>
    public int Fallbacks => (_highlightTree?.Fallbacks ?? 0) + (_outlineTree?.Fallbacks ?? 0);

    /// <summary>
    /// Follows one edit into the buffer and the trees. Returns false where the edit could not be
    /// mapped at all, which leaves this unusable and is the caller's cue to build a fresh one from
    /// the document's text.
    /// </summary>
    public bool Follow(TextEdit inverse, string inserted)
    {
        // An edit that removed and inserted nothing still reaches every listener, and re-parsing
        // for one would be a full buffer copy to arrive back where we started.
        if (inverse.Range.IsEmpty && inverse.Replacement.Length == 0 && inserted.Length == 0) return true;

        TreeSitter.Bindings.TSInputEdit edit;
        try
        {
            edit = _buffer.Follow(inverse, inserted);
        }
        catch (Exception)
        {
            return false;
        }

        var utf8 = _buffer.Utf8;
        _highlightTree?.Advance(utf8, in edit);
        _outlineTree?.Advance(utf8, in edit);
        return true;
    }

    /// <summary>Throws the trees away and takes the text as a file this has not seen — for a reload,
    /// which replaces the document wholesale and produces no edits to follow.</summary>
    public void Reset(string text)
    {
        _buffer = new ParseBuffer(text);
        _highlightTree?.Discard();
        _outlineTree?.Discard();
    }

    /// <summary>What the file now says about itself: its colouring and its declarations, each from
    /// its own tree over the same bytes.</summary>
    public EditorAnnotations Read()
    {
        var utf8 = _buffer.Utf8;
        var text = _buffer.Text();

        var spans = _highlightTree is { } highlight
            ? _highlighter.Highlight(_languageId, text, utf8, highlight.Current(utf8))
            : null;

        var outline = _outlineTree is { } outlining && _outlineLanguage is { } language
            ? _extractor.Extract(language, text, utf8, outlining.Current(utf8))
            : null;

        return new EditorAnnotations(spans is null ? null : new DiffHighlight(null, spans), outline);
    }

    public void Dispose()
    {
        _highlightTree?.Dispose();
        _outlineTree?.Dispose();
    }
}
