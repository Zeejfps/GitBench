using GitBench.Features.FileBrowser;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// Finding a file by name in the rail. The feature is one list standing in for another, so most of
/// what matters is the seam between them: what the rail is showing, what the cursor and the preview
/// follow while it is showing it, and what is left behind when the search ends.
/// </summary>
public class FileFinderTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-file-finder-");
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly List<FileBrowserUiState> _persisted = [];
    private readonly ScriptedCatalog _catalog = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void AQueryPutsTheMatchesOnTheRailInPlaceOfTheTree()
    {
        Write("README.md");
        Write("src/AuthService.cs");
        Write("tests/AuthServiceTests.cs");
        using var browser = Browser();

        Find(browser, "AuthService");

        Assert.Equal(
            ["AuthService.cs", "AuthServiceTests.cs"],
            browser.Rows.Value.Select(r => r.Name).ToArray());
    }

    // Two files of the same name are otherwise the same row twice, so the directory rides along as
    // the row's dimmed second half rather than being left to the tree's indentation to say.
    [Fact]
    public void AMatchCarriesTheDirectoryItWasFoundIn()
    {
        Write("src/App/Program.cs");
        Write("Program.cs");
        using var browser = Browser();

        Find(browser, "Program.cs");

        Assert.Equal(
            [("Program.cs", null), ("Program.cs", "src/App")],
            browser.Rows.Value.Select(r => (r.Name, r.Detail)).ToArray());
    }

    // Results are flat and unopenable: a chevron on one would offer to expand a row that has no
    // place in the tree it is standing in front of.
    [Fact]
    public void AMatchIsAFlatRowWithNothingToOpen()
    {
        Write("src/AuthService.cs");
        using var browser = Browser();

        Find(browser, "Auth");

        var row = Assert.IsType<FileBrowserRow.File>(Assert.Single(browser.Rows.Value));
        Assert.Equal(0, row.Depth);
        Assert.False(row.IsExpandable);
    }

    [Fact]
    public void AQueryNothingAnswersLeavesTheRailEmptyRatherThanShowingTheTree()
    {
        Write("README.md");
        using var browser = Browser();

        Find(browser, "nothing-is-called-this");

        Assert.Empty(browser.Rows.Value);
    }

    // An open field is not yet a question.
    [Fact]
    public void OpeningTheFieldWithoutTypingLeavesTheTreeWhereItWas()
    {
        Write("README.md");
        using var browser = Browser();
        var tree = browser.Rows.Value;

        browser.Finder.Open();
        Settle(browser);

        Assert.Same(tree, browser.Rows.Value);
    }

    [Fact]
    public void ClosingTheFinderPutsTheTreeBack()
    {
        Write("README.md");
        Write("src/AuthService.cs");
        using var browser = Browser();
        Find(browser, "Auth");
        Assert.Single(browser.Rows.Value);

        browser.Finder.Close();
        Settle(browser);

        Assert.Equal(["src", "README.md"], browser.Rows.Value.Select(r => r.Name).ToArray());
        Assert.Equal(string.Empty, browser.Finder.Text.Value);
    }

    // The cursor moving through results previews what it lands on, the same as it does in the tree —
    // arrowing down a list of matches is looking, not opening.
    [Fact]
    public void MovingTheCursorThroughTheResultsPreviewsWhatItLandsOn()
    {
        Write("src/AuthService.cs", "class AuthService { }");
        using var browser = Browser();
        Find(browser, "AuthService");

        browser.MoveCursor(1);
        WaitFor(browser, () => browser.Preview.Value is FilePreview.Text);

        var text = Assert.IsType<FilePreview.Text>(browser.Preview.Value);
        Assert.Equal(At("src/AuthService.cs"), text.Path);
    }

    // Enter ends the search, and the tree opens onto where the file lives — otherwise closing the
    // finder would leave the reader looking at a rail that has forgotten where they just went.
    [Fact]
    public void OpeningAMatchEndsTheSearchAndRevealsTheFileInTheTree()
    {
        Write("src/App/Program.cs", "class Program { }");
        using var browser = Browser();
        Find(browser, "Program");

        browser.ActivateBestMatch();
        WaitFor(browser, () => browser.Cursor.Value == At("src/App/Program.cs"));

        Assert.False(browser.Finder.IsOpen.Value);
        Assert.Equal(
            ["src", "App", "Program.cs"],
            browser.Rows.Value.Select(r => r.Name).ToArray());
        Assert.False(browser.Tabs.Single().Transient.Value);
    }

    [Fact]
    public void EnterWithNothingTypedOpensNothing()
    {
        Write("README.md");
        using var browser = Browser();
        browser.Finder.Open();
        Settle(browser);

        browser.ActivateBestMatch();
        Settle(browser);

        Assert.Empty(browser.Tabs);
    }

    // Past the cap the reader is scrolling a ranking rather than reading an answer, so the count
    // says there are more instead of listing them.
    [Fact]
    public void MoreMatchesThanTheRailListsAreReportedAsATruncatedRanking()
    {
        _catalog.Files = Enumerable
            .Range(0, FileFinderViewModel.MaxResults + 20)
            .Select(i => $"src/Widget{i}.cs")
            .ToArray();
        using var browser = Browser();

        Find(browser, "Widget");

        Assert.Equal(FileFinderViewModel.MaxResults, browser.Finder.Results.Value.Paths.Count);
        Assert.True(browser.Finder.Results.Value.Truncated);
    }

    // The catalog is a listing of the whole repository. Reading it per keystroke is the one thing
    // that would make this feature cost more than it saves.
    [Fact]
    public void TheCatalogIsReadOncePerSearchRatherThanPerKeystroke()
    {
        Write("src/AuthService.cs");
        using var browser = Browser();

        Find(browser, "A");
        Find(browser, "Au");
        Find(browser, "Aut");

        Assert.Equal(1, _catalog.Reads);
    }

    // A search is short, and what it can reach is what was there when it opened; the next one starts
    // by looking again, which is what makes a file created a moment ago findable.
    [Fact]
    public void TheNextSearchLooksAtTheRepositoryAgain()
    {
        Write("src/AuthService.cs");
        using var browser = Browser();
        Find(browser, "Auth");
        browser.Finder.Close();

        Find(browser, "Auth");

        Assert.Equal(2, _catalog.Reads);
    }

    private void Find(FileBrowserViewModel browser, string query)
    {
        browser.Finder.Open();
        browser.Finder.SetText(query);
        Settle(browser);
    }

    private string At(string relative) =>
        Path.GetFullPath(Path.Combine(_dir.Path, relative.Replace('/', Path.DirectorySeparatorChar)));

    private void Write(string relative, params string[] lines)
    {
        var path = At(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
        _catalog.Add(relative);
    }

    private FileBrowserViewModel Browser()
    {
        var browser = new FileBrowserViewModel(
            new Repo(Guid.NewGuid(), _dir.Path, "repo"),
            new FileSystemReader(),
            FileBrowserFakes.NoIgnore,
            _catalog.List,
            new UnparsedFiles(),
            _dispatcher,
            new FileBrowserUiState(),
            _persisted.Add,
            TestDocuments.ForOneRepo(),
            TestDocuments.Discard,
            TestDocuments.KeepEdits);

        browser.Invalidate();
        Settle(browser);
        return browser;
    }

    private void Settle(FileBrowserViewModel browser)
    {
        for (var i = 0; i < 10; i++)
        {
            browser.Pending.Wait(TimeSpan.FromSeconds(5));
            browser.Finder.Pending.Wait(TimeSpan.FromSeconds(5));
            _dispatcher.Drain();
        }
    }

    private void WaitFor(FileBrowserViewModel browser, Func<bool> until)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            Settle(browser);
            if (until()) return;
            Thread.Sleep(5);
        }

        Assert.Fail("The browser never reached the expected state.");
    }

    /// <summary>The repository's files as git would list them, without a git.</summary>
    private sealed class ScriptedCatalog
    {
        private readonly List<string> _files = [];

        public int Reads { get; private set; }

        public IReadOnlyList<string> Files
        {
            get => _files;
            set { _files.Clear(); _files.AddRange(value); }
        }

        public void Add(string relative) => _files.Add(relative);

        public IReadOnlyList<string> List()
        {
            Reads++;
            return [.. _files];
        }
    }
}
