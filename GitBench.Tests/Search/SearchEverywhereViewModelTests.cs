using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Features.FileBrowser;
using GitBench.Features.Repos;
using GitBench.Features.Search;
using GitBench.Git;
using GitBench.Lsp;
using Xunit;

namespace GitBench.Tests.Search;

/// <summary>
/// Search everywhere's view model over a temp repository: files, the index's symbols and scripted
/// language servers, tabs, reopening, and opening a result.
/// </summary>
[Collection(nameof(CodeIntelCollection))]
public sealed class SearchEverywhereViewModelTests : IDisposable
{
    private static readonly LanguageId CSharp = LanguageId.Of("csharp");

    private readonly TempDir _dir = new("gitbench-search-everywhere-");
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly ManualTimeProvider _clock = new();
    private readonly FixedSymbolIndex _index = new();
    private readonly ScriptedWorkspaceSymbols _servers = new();
    private readonly RepoRegistry _registry;
    private readonly FileBrowserViewModel _browser;
    private readonly OneBrowser _browsers;
    private readonly SearchEverywhereViewModel _search;
    private readonly Repo _repo;

    public SearchEverywhereViewModelTests(CodeIntelFixture codeIntel)
    {
        var root = Path.Combine(_dir.Path, "repo");
        Directory.CreateDirectory(root);
        TestGit.Init(root);
        Write(root, "src/Alpha.cs", "class Alpha\n{\n    void Run() { }\n}\n");
        Write(root, "src/Beta.cs", "class Beta { }\n");
        Write(root, "docs/alphabet.md", "# Letters\n");

        var statePath = Path.Combine(_dir.Path, "state.json");
        _registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
        _registry.Open(root);
        _repo = _registry.Active.Value!;

        _browser = new FileBrowserViewModel(
            _repo,
            new FileSystemReader(),
            FileBrowserFakes.NoIgnore,
            FileBrowserFakes.EmptyCatalog,
            codeIntel.Extractor,
            codeIntel.Colors,
            _dispatcher,
            new FileBrowserUiState(),
            _ => { },
            TestDocuments.ForOneRepo(),
            TestDocuments.Discard,
            TestDocuments.KeepEdits);
        _browsers = new OneBrowser(_browser);

        _search = new SearchEverywhereViewModel(
            _registry,
            new GitService(new NullActivityTracker()),
            _index,
            _servers,
            _browsers,
            _dispatcher,
            _clock);
    }

    public void Dispose()
    {
        _search.Dispose();
        _browsers.Dispose();
        _browser.Dispose();
        _registry.Dispose();
        _dir.Dispose();
    }

    private static void Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static SymbolRow Symbol(string name, SymbolKind kind, string path, int line, string? container = null) =>
        new(name, kind, container, null, path, new FileLine(line), new RawColumn(4));

    private void Index(params SymbolRow[] rows) =>
        _index.Snapshot.Value = new SymbolIndexSnapshot(_repo.Id, [rows], new SymbolIndexProgress.Complete());

    private void Search(string query)
    {
        _search.Open();
        _search.SetQuery(query);
    }

    private void Until(Func<bool> done, string what) => Pump.WaitFor(_dispatcher, done, what);

    private IReadOnlyList<SearchRow> Rows => _search.Rows.Value;

    private void PassServerDelay()
    {
        _clock.Advance(SearchEverywhereViewModel.ServerDelay);
        Until(() => _servers.Asked.Count > 0, "the servers to be asked");
    }

    [Fact]
    public void TheFilesTab_RanksTheWorkingTreesFiles_WithTheNameLettersMatched()
    {
        _search.Open();
        _search.SetTab(SearchTab.Files);
        _search.SetQuery("Alpha");

        Until(() => Rows.Count > 0, "file results");

        var first = Assert.IsType<SearchRow.FileHit>(Rows[0]);
        Assert.Equal("src/Alpha.cs", first.Path);
        Assert.Equal([0, 1, 2, 3, 4], first.NameHighlights);
        Assert.Contains(Rows, r => r is SearchRow.FileHit { Path: "docs/alphabet.md" });
    }

    [Fact]
    public void APathWithALineAndColumn_OpensThere()
    {
        _search.Open();
        _search.SetTab(SearchTab.Files);
        _search.SetQuery("Alpha.cs:3:5");

        Until(() => Rows.Count > 0, "file results");

        var hit = Assert.IsType<SearchRow.FileHit>(Rows[0]);
        Assert.Equal("src/Alpha.cs", hit.Path);
        Assert.Equal(new TextPosition(new FileLine(3), new RawColumn(4)), hit.At);
    }

    [Fact]
    public void TheTypesTab_ListsOnlyTypes()
    {
        Index(Symbol("Alpha", SymbolKind.Class, "src/Alpha.cs", 1), Symbol("AlphaRun", SymbolKind.Method, "src/Alpha.cs", 3, "Alpha"));
        _search.Open();
        _search.SetTab(SearchTab.Types);
        _search.SetQuery("Alpha");

        Until(() => Rows.Count > 0, "type results");

        var only = Assert.IsType<SearchRow.Symbol>(Assert.Single(Rows));
        Assert.Equal("Alpha", only.Hit.Row.Name);
    }

    [Fact]
    public void TheAllTab_GroupsTypesFilesAndMembers_WithAMoreRowPastTheGroupSize()
    {
        var members = Enumerable.Range(0, 7).Select(i => Symbol($"AlphaMember{i}", SymbolKind.Method, "src/Alpha.cs", i + 1, "Alpha"));
        Index([Symbol("Alpha", SymbolKind.Class, "src/Alpha.cs", 1), .. members]);
        Search("Alpha");

        Until(() => Rows.OfType<SearchRow.FileHit>().Any() && Rows.OfType<SearchRow.Symbol>().Count() > 1, "all three groups");

        var headings = Rows.OfType<SearchRow.Heading>().Select(h => h.Group).ToList();
        Assert.Equal([SearchTab.Types, SearchTab.Files, SearchTab.Symbols], headings);
        Assert.Equal(new SearchRow.More(SearchTab.Symbols), Rows[^1]);
        Assert.Equal(SearchEverywhereViewModel.GroupSize, Rows.OfType<SearchRow.Symbol>().Count(s => !s.Hit.Row.IsType));
    }

    [Fact]
    public void TheSelection_SkipsHeadings()
    {
        Index(Symbol("Alpha", SymbolKind.Class, "src/Alpha.cs", 1));
        Search("Alpha");
        Until(() => Rows.OfType<SearchRow.FileHit>().Any() && Rows.OfType<SearchRow.Symbol>().Any(), "results");

        Assert.IsType<SearchRow.Symbol>(Rows[_search.Selected.Value]);
        _search.Move(1);
        Assert.IsType<SearchRow.FileHit>(Rows[_search.Selected.Value]);
        _search.Move(-5);
        Assert.IsType<SearchRow.Symbol>(Rows[_search.Selected.Value]);
    }

    [Fact]
    public void TheTopRowStaysSelected_AsSlowerSourcesArriveAboveIt()
    {
        Search("Alpha");
        Until(() => Rows.OfType<SearchRow.FileHit>().Any(), "file results");
        Assert.IsType<SearchRow.FileHit>(Rows[_search.Selected.Value]);

        Index(Symbol("Alpha", SymbolKind.Class, "src/Alpha.cs", 1));
        Until(() => Rows.OfType<SearchRow.Symbol>().Any(), "the index's rows");

        Assert.Equal(1, _search.Selected.Value);
        Assert.IsType<SearchRow.Symbol>(Rows[1]);
    }

    [Fact]
    public void ARowTheReaderMovedTo_StaysSelected_AsRowsArriveAboveIt()
    {
        Search("Alpha");
        Until(() => Rows.OfType<SearchRow.FileHit>().Count() > 1, "file results");
        _search.Move(1);
        var chosen = Assert.IsType<SearchRow.FileHit>(Rows[_search.Selected.Value]).Path;

        Index(Symbol("Alpha", SymbolKind.Class, "src/Alpha.cs", 1));
        Until(() => Rows.OfType<SearchRow.Symbol>().Any(), "the index's rows");

        Assert.Equal(chosen, Assert.IsType<SearchRow.FileHit>(Rows[_search.Selected.Value]).Path);
    }

    [Fact]
    public void MoreSwitchesToThatGroupsTab()
    {
        Index(Enumerable.Range(0, 8).Select(i => Symbol($"AlphaType{i}", SymbolKind.Class, "src/Alpha.cs", i + 1)).ToArray());
        Search("AlphaType");
        Until(() => Rows.OfType<SearchRow.More>().Any(), "a more row");

        _search.ActivateAt(Rows.ToList().FindIndex(r => r is SearchRow.More), OpenAs.Pinned);

        Assert.Equal(SearchTab.Types, _search.Tab.Value);
        Assert.Equal(8, Rows.Count);
    }

    [Fact]
    public void TabCyclesThroughTheTabs_BothWays()
    {
        _search.Open();

        _search.CycleTab(1);
        Assert.Equal(SearchTab.Types, _search.Tab.Value);
        _search.CycleTab(-1);
        _search.CycleTab(-1);
        Assert.Equal(SearchTab.Files, _search.Tab.Value);
    }

    [Fact]
    public void Reopening_RestoresTheLastQueryAndTab()
    {
        _search.Open();
        _search.SetTab(SearchTab.Symbols);
        _search.SetQuery("Beta");
        _search.Close();
        Assert.False(_search.IsOpen.Value);

        _search.Open();

        Assert.Equal("Beta", _search.Query.Value);
        Assert.Equal(SearchTab.Symbols, _search.Tab.Value);
    }

    [Fact]
    public void AServersAnswer_LeadsItsLanguage_WithoutDuplicatingTheIndex()
    {
        _servers.Running[CSharp] = ".cs";
        Index(Symbol("Alpha", SymbolKind.Class, "src/Alpha.cs", 1), Symbol("AlphaOld", SymbolKind.Class, "src/Old.cs", 1));
        _search.Open();
        _search.SetTab(SearchTab.Types);
        _search.SetQuery("Alpha");
        Until(() => Rows.Count == 2, "index results");

        PassServerDelay();
        Assert.Equal(["Alpha"], _servers.Asked);
        _servers.Answer(CSharp, [Symbol("Alpha", SymbolKind.Class, "src/Alpha.cs", 1)]);
        Until(() => Rows.OfType<SearchRow.Symbol>().Any(s => s.Hit.Source == SymbolSource.Server), "the server's rows");

        Assert.Equal(
            [("Alpha", SymbolSource.Server), ("AlphaOld", SymbolSource.Index)],
            Rows.OfType<SearchRow.Symbol>().Select(s => (s.Hit.Row.Name, s.Hit.Source)));
    }

    [Fact]
    public void AnAnswerToAnOlderQuery_IsDropped()
    {
        _servers.Running[CSharp] = ".cs";
        Index(Symbol("Beta", SymbolKind.Class, "src/Beta.cs", 1));
        _search.Open();
        _search.SetTab(SearchTab.Types);
        _search.SetQuery("Alpha");
        PassServerDelay();

        _search.SetQuery("Beta");
        Until(() => Rows.Count == 1, "index results for the new query");
        _servers.Answer(CSharp, [Symbol("Alpha", SymbolKind.Class, "src/Alpha.cs", 1)]);
        Thread.Sleep(50);
        _dispatcher.Drain();

        var only = Assert.IsType<SearchRow.Symbol>(Assert.Single(Rows));
        Assert.Equal(("Beta", SymbolSource.Index), (only.Hit.Row.Name, only.Hit.Source));
    }

    [Fact]
    public void AServerThatNeverAnswers_LeavesTheIndexRows()
    {
        _servers.Running[CSharp] = ".cs";
        Index(Symbol("Alpha", SymbolKind.Class, "src/Alpha.cs", 1));
        _search.Open();
        _search.SetTab(SearchTab.Types);
        _search.SetQuery("Alpha");
        PassServerDelay();
        _dispatcher.Drain();

        var only = Assert.IsType<SearchRow.Symbol>(Assert.Single(Rows));
        Assert.Equal(SymbolSource.Index, only.Hit.Source);
    }

    [Fact]
    public void TypingWithinTheDelay_AsksTheServersOnce()
    {
        _servers.Running[CSharp] = ".cs";
        _search.Open();
        _search.SetQuery("A");
        _clock.Advance(TimeSpan.FromMilliseconds(100));
        _search.SetQuery("Al");
        _clock.Advance(TimeSpan.FromMilliseconds(100));
        _search.SetQuery("Alp");
        PassServerDelay();
        Thread.Sleep(50);
        _dispatcher.Drain();

        Assert.Equal(["Alp"], _servers.Asked);
    }

    [Fact]
    public void OpeningASymbol_OpensItsFile_WithTheCaretOnTheName_AndCloses()
    {
        Index(Symbol("Alpha", SymbolKind.Class, "src/Alpha.cs", 1));
        _search.Open();
        _search.SetTab(SearchTab.Types);
        _search.SetQuery("Alpha");
        Until(() => Rows.Count == 1, "the symbol");

        _search.Activate(OpenAs.Pinned);

        Assert.False(_search.IsOpen.Value);
        var request = _browser.CaretRequest.Value!;
        Assert.EndsWith("src/Alpha.cs", request.Path.Replace((char)92, (char)47), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new TextPosition(new FileLine(1), new RawColumn(4)), request.At);
        Assert.False(Assert.Single(_browser.Tabs).Transient.Value);
    }

    [Fact]
    public void ShiftEnter_OpensInAPreviewTab()
    {
        _search.Open();
        _search.SetTab(SearchTab.Files);
        _search.SetQuery("Beta");
        Until(() => Rows.Count > 0, "file results");

        _search.Activate(OpenAs.Transient);

        Assert.True(Assert.Single(_browser.Tabs).Transient.Value);
    }

    [Fact]
    public void SwitchingRepository_ClosesSearch()
    {
        _search.Open();
        var other = Path.Combine(_dir.Path, "other");
        Directory.CreateDirectory(other);
        TestGit.Init(other);

        _registry.Open(other);

        Assert.False(_search.IsOpen.Value);
    }
}
