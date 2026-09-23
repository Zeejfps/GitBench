using ZGF.Observable;

namespace GitBench.Features.FileBrowser;

/// <summary>What one query found: the ranked repo-relative paths, and whether the ranking stopped
/// short of everything that matched.</summary>
internal readonly record struct FileFinderResults(IReadOnlyList<string> Paths, bool Truncated)
{
    public static readonly FileFinderResults None = new([], false);
}

/// <summary>
/// Finding a file in the working tree by name: whether the field is open, what was typed into it,
/// and the paths that came back ranked. Not to be confused with <see cref="FileSearchViewModel"/>,
/// which searches inside the file already on screen.
/// </summary>
/// <remarks>
/// <para>
/// The catalog is read once per open and dropped on close, rather than kept and invalidated. A
/// working tree announces a change twice a minute whether or not anything moved, and re-listing
/// every path in the repository on that schedule would cost more than the whole feature saves. A
/// search is short: the files it can reach are the ones that existed when the field opened.
/// </para>
/// <para>
/// Both the listing and the ranking run off the UI thread and land through the dispatcher, so a
/// repository with a hundred thousand paths does not stall a keystroke. A result that arrives after
/// the query has moved on is dropped by generation rather than published late.
/// </para>
/// </remarks>
internal sealed class FileFinderViewModel : IDisposable
{
    /// <summary>How many matches the rail will list. Past this the reader is scrolling a ranking
    /// rather than reading an answer, and the thing to do is type another letter.</summary>
    public const int MaxResults = 200;

    private readonly Func<IReadOnlyList<string>> _listFiles;
    private readonly IUiDispatcher _dispatcher;

    private readonly State<bool> _isOpen = new(false);
    private readonly State<string> _text = new(string.Empty);
    private readonly State<FileFinderResults> _results = new(FileFinderResults.None);

    private Task<IReadOnlyList<string>>? _catalogued;
    private Task _pending = Task.CompletedTask;
    private int _generation;
    private bool _disposed;

    public FileFinderViewModel(Func<IReadOnlyList<string>> listFiles, IUiDispatcher dispatcher)
    {
        _listFiles = listFiles;
        _dispatcher = dispatcher;
    }

    public IReadable<bool> IsOpen => _isOpen;

    public IReadable<string> Text => _text;

    public IReadable<FileFinderResults> Results => _results;

    /// <summary>Whether the rail is answering a question rather than listing the tree. An open field
    /// with nothing typed into it is not yet a question.</summary>
    public bool IsFiltering => _isOpen.Value && _text.Value.Trim().Length > 0;

    /// <summary>The tail of the work one query started. Held so a test can wait for the listing and
    /// the ranking rather than sleeping for them; nothing in the application awaits it.</summary>
    internal Task Pending => _pending;

    /// <summary>Anything that changes which rows the rail should be showing.</summary>
    public event Action? Changed;

    /// <summary>
    /// The field should take the caret back, with what it is holding selected — a second press of
    /// the shortcut leaves the old query visible and lets the next keystroke replace it.
    /// </summary>
    public event Action? RefocusRequested;

    public void Open()
    {
        if (_disposed) return;
        if (_isOpen.Value)
        {
            RefocusRequested?.Invoke();
            return;
        }

        _isOpen.Value = true;
        _pending = _catalogued ??= Task.Run(() => WorkingTreeFileSearch.List(_listFiles));
        Changed?.Invoke();
    }

    /// <summary>
    /// Closes the field and forgets the query with it. Unlike find-in-file, where a query outlives
    /// the bar, a standing query here would put a list of results back on screen the next time the
    /// field opened — and what the rail is for is the tree.
    /// </summary>
    public void Close()
    {
        if (_disposed || !_isOpen.Value) return;

        _generation++;
        _isOpen.Value = false;
        _text.Value = string.Empty;
        _results.Value = FileFinderResults.None;
        _catalogued = null;
        Changed?.Invoke();
    }

    public void SetText(string text)
    {
        if (_disposed || _text.Value == text) return;
        _text.Value = text;
        Rank();
        Changed?.Invoke();
    }

    private void Rank()
    {
        var generation = ++_generation;
        var query = _text.Value.Trim();
        if (query.Length == 0 || _catalogued is not { } catalogued)
        {
            _results.Value = FileFinderResults.None;
            return;
        }

        _pending = catalogued.ContinueWith(
            listed =>
            {
                var results = WorkingTreeFileSearch.Rank(listed.Result, query, MaxResults);
                _dispatcher.Post(() =>
                {
                    if (_disposed || generation != _generation) return;
                    _results.Value = results;
                    Changed?.Invoke();
                });
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _isOpen.Dispose();
        _text.Dispose();
        _results.Dispose();
    }
}
