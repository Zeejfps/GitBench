using System.Text.RegularExpressions;
using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Features.FileBrowser;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Lsp;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Search;

internal enum SearchTab
{
    All,
    Types,
    Symbols,
    Files,
}

/// <summary>One row of the results list.</summary>
internal abstract record SearchRow
{
    private SearchRow() { }

    /// <summary>A group's title, on the All tab.</summary>
    public sealed record Heading(SearchTab Group) : SearchRow;

    /// <param name="Path">Repo-relative.</param>
    /// <param name="NameHighlights">Matched characters of the file's name.</param>
    /// <param name="At">Where in the file the query asked to land, from a <c>path:line</c> query.</param>
    public sealed record FileHit(string Path, IReadOnlyList<int> NameHighlights, TextPosition? At) : SearchRow;

    public sealed record Symbol(SymbolHit Hit) : SearchRow;

    /// <summary>The rest of an All-tab group, on its own tab.</summary>
    public sealed record More(SearchTab Tab) : SearchRow;

    public bool IsSelectable => this is not Heading;
}

/// <summary>
/// Search everywhere: files, types and members of the active repository, ranked as the query is
/// typed, from the working tree's file list, the symbol index and whatever language servers are
/// already running.
/// </summary>
/// <remarks>
/// <para>
/// Every source is asked off the UI thread and answers through the dispatcher, tagged with the query
/// it was asked about; an answer to an older query is dropped rather than merged. A new query keeps
/// the old rows on screen until its own arrive, and a server's answer replaces rows in place, so the
/// list does not flicker.
/// </para>
/// <para>
/// The file list is read once per open, like find file's. The query and tab are kept per repository
/// for the app session, so search reopens where it was left, with the query selected.
/// </para>
/// </remarks>
internal sealed class SearchEverywhereViewModel : IDisposable
{
    public const int MaxRows = 200;

    /// <summary>How many rows a group of the All tab shows before its "more" row.</summary>
    public const int GroupSize = 5;

    public static readonly TimeSpan ServerDelay = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan ServerLimit = TimeSpan.FromSeconds(2);

    private static readonly Regex PathAndPlace = new(@"^(?<path>.+?):(?<line>\d+)(?::(?<column>\d+))?$", RegexOptions.CultureInvariant);

    private readonly IRepoRegistry _registry;
    private readonly IGitRepositoryReader _git;
    private readonly ISymbolIndexStore _index;
    private readonly IWorkspaceSymbolSource _servers;
    private readonly IFileBrowserStore _browsers;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;

    private readonly State<bool> _isOpen = new(false);
    private readonly State<string> _query = new(string.Empty);
    private readonly Derived<bool> _foundNothing;
    private readonly State<IReadOnlyList<SearchRow>> _rows = new([]);
    private readonly State<int> _selected = new(-1);
    private readonly Dictionary<Guid, (string Query, SearchTab Tab)> _remembered = new();
    private readonly List<IDisposable> _subscriptions = new();

    private Repo? _repo;
    private Task<IReadOnlyList<string>>? _files;
    private CancellationTokenSource? _serverAsk;
    private int _generation;
    private int _indexRanking;
    private bool _chosen;
    private IReadOnlyList<SearchRow.FileHit> _fileHits = [];
    private IReadOnlyList<RankedSymbol> _indexTypes = [];
    private IReadOnlyList<RankedSymbol> _indexAll = [];
    private Dictionary<LanguageId, IReadOnlyList<SymbolRow>> _answers = new();
    private bool _disposed;

    public SearchEverywhereViewModel(
        IRepoRegistry registry,
        IGitRepositoryReader git,
        ISymbolIndexStore index,
        IWorkspaceSymbolSource servers,
        IFileBrowserStore browsers,
        IUiDispatcher dispatcher,
        TimeProvider time)
    {
        _registry = registry;
        _git = git;
        _index = index;
        _servers = servers;
        _browsers = browsers;
        _dispatcher = dispatcher;
        _time = time;

        _foundNothing = new Derived<bool>(() => _rows.Value.Count == 0 && _query.Value.Trim().Length > 0);
        _subscriptions.Add(_foundNothing);
        _subscriptions.Add(Tab.Subscribe(_ =>
        {
            _chosen = false;
            if (_isOpen.Value) Publish();
        }));
        _subscriptions.Add(registry.Active.Subscribe(_ => { if (_isOpen.Value) Close(); }));
        _subscriptions.Add(index.Active.Subscribe(_ => { if (_isOpen.Value) RankIndex(_generation); }));
    }

    public IReadable<bool> IsOpen => _isOpen;

    public IReadable<string> Query => _query;

    /// <summary>The tab on screen. Writable, so a tab strip can bind to it.</summary>
    public State<SearchTab> Tab { get; } = new(SearchTab.All);

    /// <summary>A query that no source has found anything for, yet.</summary>
    public IReadable<bool> FoundNothing => _foundNothing;

    public IReadable<IReadOnlyList<SearchRow>> Rows => _rows;

    /// <summary>The index of the selected row, or -1 while no row is selectable.</summary>
    public IReadable<int> Selected => _selected;

    /// <summary>The index behind the symbol rows, for how far it has got.</summary>
    public IReadable<SymbolIndexSnapshot> Index => _index.Active;

    /// <summary>The field should take the caret, with the query selected.</summary>
    public event Action? FocusRequested;

    public void Open()
    {
        if (_disposed) return;
        if (_isOpen.Value)
        {
            FocusRequested?.Invoke();
            return;
        }

        if (_registry.Active.Value is not { } repo) return;
        _repo = repo;
        var (query, tab) = _remembered.TryGetValue(repo.Id, out var kept) ? kept : (string.Empty, SearchTab.All);
        _query.Value = query;
        Tab.Value = tab;
        _files = Task.Run(() => WorkingTreeFileSearch.List(() => _git.ListWorkingTreeFiles(repo)));
        _isOpen.Value = true;
        Requery();
    }

    public void Close()
    {
        if (_disposed || !_isOpen.Value) return;
        if (_repo is { } repo) _remembered[repo.Id] = (_query.Value, Tab.Value);

        _generation++;
        _serverAsk?.Cancel();
        _serverAsk = null;
        _files = null;
        _repo = null;
        _fileHits = [];
        _indexTypes = [];
        _indexAll = [];
        _answers = new Dictionary<LanguageId, IReadOnlyList<SymbolRow>>();
        _rows.Value = [];
        _selected.Value = -1;
        _isOpen.Value = false;
    }

    public void SetQuery(string text)
    {
        if (_disposed || !_isOpen.Value || _query.Value == text) return;
        _query.Value = text;
        Requery();
    }

    public void SetTab(SearchTab tab)
    {
        if (!_disposed) Tab.Value = tab;
    }

    /// <summary>Steps to the next tab, or the previous one for a negative step, wrapping around.</summary>
    public void CycleTab(int step)
    {
        var tabs = Enum.GetValues<SearchTab>();
        var at = Array.IndexOf(tabs, Tab.Value);
        SetTab(tabs[((at + step) % tabs.Length + tabs.Length) % tabs.Length]);
    }

    /// <summary>Moves the selection by a number of selectable rows, stopping at either end.</summary>
    public void Move(int delta)
    {
        var rows = _rows.Value;
        var at = _selected.Value;
        var step = Math.Sign(delta);
        for (var remaining = Math.Abs(delta); remaining > 0; remaining--)
        {
            var next = at + step;
            while (next >= 0 && next < rows.Count && !rows[next].IsSelectable) next += step;
            if (next < 0 || next >= rows.Count) break;
            at = next;
        }

        _chosen = true;
        if (at != _selected.Value) _selected.Value = at;
    }

    public void Select(int index)
    {
        if (index < 0 || index >= _rows.Value.Count || !_rows.Value[index].IsSelectable) return;
        _chosen = true;
        _selected.Value = index;
    }

    public void Activate(OpenAs how) => ActivateAt(_selected.Value, how);

    /// <summary>Opens what a row stands for: a file, a symbol's declaration, or a group's own tab.</summary>
    public void ActivateAt(int index, OpenAs how)
    {
        var rows = _rows.Value;
        if (index < 0 || index >= rows.Count) return;

        switch (rows[index])
        {
            case SearchRow.Heading:
                return;
            case SearchRow.More more:
                SetTab(more.Tab);
                return;
            case SearchRow.FileHit file:
                OpenFile(file.Path, file.At, how);
                return;
            case SearchRow.Symbol symbol:
                var row = symbol.Hit.Row;
                OpenFile(row.Path, new TextPosition(row.Line, row.Column), how);
                return;
            default:
                throw new InvalidOperationException($"No rule for {rows[index].GetType().Name}.");
        }
    }

    private void OpenFile(string relativePath, TextPosition? at, OpenAs how)
    {
        if (_repo is not { } repo || _browsers.Active.Value is not { } browser) return;
        var absolute = System.IO.Path.Combine(repo.Path, relativePath);
        Close();
        browser.OpenSearchResult(absolute, at, how);
    }

    private void Requery()
    {
        _chosen = false;
        var generation = ++_generation;
        _serverAsk?.Cancel();
        _serverAsk = null;
        _answers = new Dictionary<LanguageId, IReadOnlyList<SymbolRow>>();

        var text = _query.Value.Trim();
        if (text.Length == 0)
        {
            _fileHits = [];
            _indexTypes = [];
            _indexAll = [];
            Publish();
            return;
        }

        RankFiles(generation, text);
        RankIndex(generation);

        var ask = new CancellationTokenSource();
        _serverAsk = ask;
        Task.Delay(ServerDelay, _time, ask.Token).ContinueWith(
            _ => _dispatcher.Post(() => AskServers(generation, text, ask.Token)),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    private void RankFiles(int generation, string text)
    {
        if (_files is not { } files) return;
        var (pathQuery, at) = ParseFileQuery(text);
        files.ContinueWith(
            listed =>
            {
                var ranked = WorkingTreeFileSearch.Rank(listed.Result, pathQuery, MaxRows);
                var queryName = LastSegment(pathQuery);
                var hits = new SearchRow.FileHit[ranked.Paths.Count];
                for (var i = 0; i < hits.Length; i++)
                {
                    var path = ranked.Paths[i];
                    var highlights = SymbolSearch.Match(queryName, LastSegment(path))?.Highlights ?? [];
                    hits[i] = new SearchRow.FileHit(path, highlights, at);
                }

                _dispatcher.Post(() =>
                {
                    if (_disposed || generation != _generation) return;
                    _fileHits = hits;
                    Publish();
                });
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    private void RankIndex(int generation)
    {
        var text = _query.Value.Trim();
        var snapshot = _index.Active.Value;
        if (text.Length == 0 || _repo is not { } repo || snapshot.RepoId != repo.Id) return;

        var ranking = ++_indexRanking;
        var query = SymbolQuery.Parse(text);
        Task.Run(() =>
        {
            var types = SymbolSearch.Rank(snapshot.Symbols, query, row => row.IsType, MaxRows);
            var all = SymbolSearch.Rank(snapshot.Symbols, query, _ => true, MaxRows);
            _dispatcher.Post(() =>
            {
                if (_disposed || generation != _generation || ranking != _indexRanking) return;
                _indexTypes = types;
                _indexAll = all;
                Publish();
            });
        });
    }

    private void AskServers(int generation, string text, CancellationToken cancel)
    {
        if (_disposed || generation != _generation || cancel.IsCancellationRequested) return;

        foreach (var question in _servers.AskWorkspaceSymbols(text, ServerLimit, cancel))
        {
            question.Answer.ContinueWith(
                answered => _dispatcher.Post(() =>
                {
                    if (_disposed || generation != _generation || answered.Result is not { } rows) return;
                    _answers[question.Language] = rows;
                    Publish();
                }),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Republishes the rows. The top row is selected until the reader picks another; after that the
    /// selection follows the row they picked while answers keep arriving for the same query.
    /// </summary>
    private void Publish()
    {
        var previous = _chosen && _selected.Value >= 0 && _selected.Value < _rows.Value.Count
            ? _rows.Value[_selected.Value]
            : null;
        var rows = Build();
        _rows.Value = rows;

        var index = previous is null ? -1 : IndexOf(rows, previous);
        if (index < 0) index = FirstSelectable(rows);
        _selected.Value = index;
    }

    private IReadOnlyList<SearchRow> Build()
    {
        if (_query.Value.Trim().Length == 0) return [];

        var query = SymbolQuery.Parse(_query.Value);
        var repoRoot = _repo?.Path ?? string.Empty;
        LanguageId? LanguageOf(string path) => _servers.LanguageOf(System.IO.Path.Combine(repoRoot, path));

        switch (Tab.Value)
        {
            case SearchTab.Types:
                return Symbols(SymbolMerge.Merge(_indexTypes, _answers, query, LanguageOf, row => row.IsType, MaxRows));
            case SearchTab.Symbols:
                return Symbols(SymbolMerge.Merge(_indexAll, _answers, query, LanguageOf, _ => true, MaxRows));
            case SearchTab.Files:
                return _fileHits;
            case SearchTab.All:
                var rows = new List<SearchRow>();
                Group(rows, SearchTab.Types,
                    Symbols(SymbolMerge.Merge(_indexTypes, _answers, query, LanguageOf, row => row.IsType, GroupSize + 1)));
                Group(rows, SearchTab.Files, _fileHits);
                Group(rows, SearchTab.Symbols,
                    Symbols(SymbolMerge.Merge(_indexAll, _answers, query, LanguageOf, row => !row.IsType, GroupSize + 1)));
                return rows;
            default:
                throw new InvalidOperationException($"No rule for {Tab.Value}.");
        }
    }

    private static IReadOnlyList<SearchRow> Symbols(IReadOnlyList<SymbolHit> hits)
    {
        var rows = new SearchRow[hits.Count];
        for (var i = 0; i < rows.Length; i++) rows[i] = new SearchRow.Symbol(hits[i]);
        return rows;
    }

    private static void Group(List<SearchRow> rows, SearchTab tab, IReadOnlyList<SearchRow> hits)
    {
        if (hits.Count == 0) return;
        rows.Add(new SearchRow.Heading(tab));
        for (var i = 0; i < Math.Min(GroupSize, hits.Count); i++) rows.Add(hits[i]);
        if (hits.Count > GroupSize) rows.Add(new SearchRow.More(tab));
    }

    private static int IndexOf(IReadOnlyList<SearchRow> rows, SearchRow row)
    {
        for (var i = 0; i < rows.Count; i++)
            if (SameRow(rows[i], row))
                return i;
        return -1;
    }

    // By what the row points at, not by value: a re-ranked row carries new highlights.
    private static bool SameRow(SearchRow a, SearchRow b) => (a, b) switch
    {
        (SearchRow.Symbol x, SearchRow.Symbol y) => x.Hit.Row == y.Hit.Row,
        (SearchRow.FileHit x, SearchRow.FileHit y) => x.Path == y.Path,
        _ => a.Equals(b),
    };

    private static int FirstSelectable(IReadOnlyList<SearchRow> rows)
    {
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].IsSelectable)
                return i;
        return -1;
    }

    /// <summary>A file query, with the place in the file split off: <c>Foo.cs:42</c> or <c>src/Foo.cs:42:7</c>.</summary>
    internal static (string Path, TextPosition? At) ParseFileQuery(string text)
    {
        var match = PathAndPlace.Match(text);
        if (!match.Success || !int.TryParse(match.Groups["line"].Value, out var line) || line < 1) return (text, null);

        var column = match.Groups["column"].Success && int.TryParse(match.Groups["column"].Value, out var c) && c >= 1
            ? c - 1
            : 0;
        return (match.Groups["path"].Value, new TextPosition(new FileLine(line), new RawColumn(column)));
    }

    private static string LastSegment(string path)
    {
        var slash = path.LastIndexOfAny(['/', '\\']);
        return slash < 0 ? path : path[(slash + 1)..];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _serverAsk?.Cancel();
        foreach (var subscription in _subscriptions) subscription.Dispose();
    }
}
