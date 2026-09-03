using GitBench.Features.Diff;
using GitBench.Git;
using ZGF.Observable;

namespace GitBench.Features.FileBrowser;

/// <summary>
/// The find bar over one repository's file preview: whether it is open, what it is looking for, and
/// which of the hits the reader is standing on.
/// </summary>
/// <remarks>
/// <para>
/// It answers "what matches and which one is current"; where that lands on screen is the body's,
/// which is why nothing here knows about rows, scroll or pixels. The two things it needs from the
/// surface — the file on screen and the line at the top of it — arrive as functions, so the whole
/// of the find behaviour is testable without a window.
/// </para>
/// <para>
/// The query outlives a close and a file switch, the way an editor's does: closing the bar drops
/// the hits, not what was typed to find them.
/// </para>
/// </remarks>
internal sealed class FileSearchViewModel
{
    private readonly Func<FilePreview> _shown;
    private readonly Func<int> _topVisibleLine;

    private readonly State<bool> _isOpen = new(false);
    private readonly State<string> _text = new(string.Empty);
    private readonly State<bool> _matchCase = new(false);
    private readonly State<bool> _wholeWord = new(false);
    private readonly State<FileSearchHits> _hits = new(FileSearchHits.None);

    public FileSearchViewModel(Func<FilePreview> shown, Func<int> topVisibleLine)
    {
        _shown = shown;
        _topVisibleLine = topVisibleLine;
    }

    public IReadable<bool> IsOpen => _isOpen;

    public IReadable<string> Text => _text;

    public IReadable<bool> MatchCase => _matchCase;

    public IReadable<bool> WholeWord => _wholeWord;

    /// <summary>What the query found, and where the cursor is in it. One slice rather than three,
    /// so a reader of it never pairs a hit list with another file's path.</summary>
    public IReadable<FileSearchHits> Hits => _hits;

    public FileSearchQuery Query => new(_text.Value, _matchCase.Value, _wholeWord.Value);

    /// <summary>The bar was asked for while it was already open — the field should take the caret
    /// back and offer the old query for replacement, the way a second Ctrl+F does in an editor.</summary>
    public event Action? RefocusRequested;

    public void Open()
    {
        if (_isOpen.Value)
        {
            RefocusRequested?.Invoke();
            return;
        }

        _isOpen.Value = true;
        Rescan(AnchorLine());
    }

    public void Close()
    {
        if (!_isOpen.Value) return;
        _isOpen.Value = false;
        _hits.Value = FileSearchHits.None;
    }

    public void SetText(string text)
    {
        if (_text.Value == text) return;
        _text.Value = text;
        Rescan(AnchorLine());
    }

    public void ToggleMatchCase()
    {
        _matchCase.Value = !_matchCase.Value;
        Rescan(AnchorLine());
    }

    public void ToggleWholeWord()
    {
        _wholeWord.Value = !_wholeWord.Value;
        Rescan(AnchorLine());
    }

    public void Next() => Step(1);

    public void Previous() => Step(-1);

    /// <summary>
    /// The preview published something new. The query stands; what it found does not.
    /// </summary>
    /// <remarks>
    /// The same file arriving again is the common case, not the exception — a preview republishes
    /// itself when its syntax highlighting and its outline land — so a re-scan of the file already
    /// on screen keeps the reader where they were standing rather than sending them back to the top.
    /// </remarks>
    public void Retarget()
    {
        if (!_isOpen.Value) return;

        var previous = _hits.Value;
        var samePath = _shown() is FilePreview.Text text && text.Path == previous.Path;
        Rescan(samePath && previous.At is { } at ? at.Line : new FileLine(1));
    }

    private void Step(int delta)
    {
        var hits = _hits.Value;
        if (hits.Count == 0) return;

        var from = Math.Max(0, hits.Current);
        _hits.Value = hits with { Current = (from + delta + hits.Count) % hits.Count };
    }

    private void Rescan(FileLine anchor) =>
        _hits.Value = _shown() is FilePreview.Text text
            ? FileSearch.In(text.Path, text.Lines, Query, anchor)
            : FileSearchHits.None;

    private FileLine AnchorLine() => new(Math.Max(1, _topVisibleLine()));
}
