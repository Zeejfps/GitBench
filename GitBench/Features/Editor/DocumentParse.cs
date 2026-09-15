using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;

namespace GitBench.Features.Editor;

/// <summary>
/// One open document as the parser sees it: the bytes, the one tree kept over them, and the
/// colouring and outline the two engines read off that tree.
/// </summary>
/// <remarks>
/// Single-threaded, and holds no reference to a document, a buffer or a view. One of these belongs
/// to one file on one worker, which is what lets it own a native tree at all.
/// </remarks>
internal sealed class DocumentParse : IDisposable
{
    private readonly TreeSitterSyntaxHighlighter _highlighter;
    private readonly TreeSitterSymbolExtractor _extractor;
    private readonly CodeLanguage _language;
    private readonly MaintainedTree? _tree;

    private ParseBuffer _buffer;

    public DocumentParse(
        TreeSitterGrammars grammars,
        TreeSitterSyntaxHighlighter highlighter,
        TreeSitterSymbolExtractor extractor,
        CodeLanguage language,
        string text)
    {
        _highlighter = highlighter;
        _extractor = extractor;
        _language = language;
        _buffer = new ParseBuffer(text);
        _tree = grammars.Track(language);
    }

    /// <summary>Whether this can say anything at all about the file. False for a language whose
    /// grammar did not load, which is the caller's cue not to keep one of these.</summary>
    public bool Tracks => _tree is not null;

    /// <summary>How many times an edit produced a tree that did not describe the buffer and the
    /// buffer had to be parsed whole instead. Zero is the assertion the equivalence tests want:
    /// equality alone would also hold if every edit fell back.</summary>
    public int Fallbacks => _tree?.Fallbacks ?? 0;

    /// <summary>
    /// Follows one edit into the buffer and the tree. Returns false where the edit could not be
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

        _tree?.Advance(_buffer.Utf8, in edit);
        return true;
    }

    /// <summary>Throws the tree away and takes the text as a file this has not seen — for a reload,
    /// which replaces the document wholesale and produces no edits to follow.</summary>
    public void Reset(string text)
    {
        _buffer = new ParseBuffer(text);
        _tree?.Discard();
    }

    /// <summary>What the file now says about itself: its colouring and its declarations, both read
    /// off the one tree.</summary>
    public DiffAnnotations Read()
    {
        if (_tree is not { } tree) return new DiffAnnotations(null, null, null);

        var utf8 = _buffer.Utf8;
        var text = _buffer.Text();
        var root = tree.Current(utf8);

        var spans = _highlighter.Highlight(_language, text, utf8, root);
        var outline = _extractor.Extract(_language, text, utf8, root);

        return new DiffAnnotations(spans is null ? null : new DiffHighlight(null, spans), outline, null);
    }

    public void Dispose() => _tree?.Dispose();
}
