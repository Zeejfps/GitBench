using GitBench.Features.Diff;
using GitBench.Features.FileBrowser;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// Find in file: what counts as a hit, and where the cursor stands among them. All of it away from
/// the window — the bar is a view of these answers, not the place they are worked out.
/// </summary>
public class FileSearchTests
{
    private static readonly string[] Sample =
    [
        "var total = 0;",              // 1
        "// TOTAL is the running sum", // 2
        "subtotal += total;",          // 3
        "return total;",               // 4
    ];

    private static FileSearchHits Find(
        string text, bool matchCase = false, bool wholeWord = false, int anchor = 1) =>
        FileSearch.In(
            "src/sum.cs", Sample, new FileSearchQuery(text, matchCase, wholeWord), new FileLine(anchor));

    [Fact]
    public void CaseIsIgnoredUnlessItIsAskedFor()
    {
        Assert.Equal(5, Find("total").Count);
        Assert.Equal(4, Find("total", matchCase: true).Count);
    }

    [Fact]
    public void WholeWordRejectsAHitInsideALongerWord()
    {
        // "subtotal" and "TOTAL" both contain the query; only one of them is a word.
        var hits = Find("total", wholeWord: true);

        Assert.Equal(new[] { 1, 2, 3, 4 }, hits.Matches.Select(m => m.Line.Value));
        Assert.Equal(4, hits.Count);
    }

    [Fact]
    public void HitsCarryTheirSpanInTheFilesOwnColumns()
    {
        var hit = Assert.Single(Find("running").Matches);

        Assert.Equal(new FileLine(2), hit.Line);
        Assert.Equal(Sample[1].IndexOf("running", StringComparison.Ordinal), hit.Start.Value);
        Assert.Equal(hit.Start.Value + "running".Length, hit.End.Value);
    }

    // "aa" occurs once in "aaa", the way it does in an editor: the scan resumes past the hit rather
    // than one character into it.
    [Fact]
    public void OverlappingOccurrencesAreCountedOnce()
    {
        var hits = FileSearch.In("x", ["aaaa"], new FileSearchQuery("aa", false, false), new FileLine(1));

        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public void AnEmptyQueryFindsNothing()
    {
        var hits = Find(string.Empty);

        Assert.Equal(0, hits.Count);
        Assert.Equal(0, hits.Ordinal);
    }

    [Fact]
    public void TheCursorLandsOnTheFirstHitAtOrAfterTheAnchor()
    {
        var hits = Find("total", anchor: 3);

        Assert.Equal(new FileLine(3), hits.At?.Line);
    }

    // Nothing below the anchor is not "nothing": the file above it is still full of hits, and an
    // editor puts you on the first of them rather than on none.
    [Fact]
    public void TheCursorWrapsToTheTopWhenTheAnchorLeavesNothingBelowIt()
    {
        var hits = Find("running", anchor: 4);

        Assert.Equal(new FileLine(2), hits.At?.Line);
    }

    [Fact]
    public void AScanStopsOnceItHasCollectedAllItWillShow()
    {
        var lines = Enumerable.Repeat("x", FileSearch.MaxMatches + 100).ToArray();

        var hits = FileSearch.In("x", lines, new FileSearchQuery("x", false, false), new FileLine(1));

        Assert.True(hits.Capped);
        Assert.Equal(FileSearch.MaxMatches, hits.Count);
    }
}

/// <summary>The find bar's own state: what it does to the cursor, and what survives a close, a step
/// and the file underneath it being republished.</summary>
public class FileSearchViewModelTests
{
    private FilePreview _shown = FilePreview.None.Instance;
    private int _topLine = 1;

    private FileSearchViewModel Model() => new(() => _shown, () => _topLine);

    private void Show(string path, params string[] lines) =>
        _shown = new FilePreview.Text(path, lines, Truncated: false, Highlight: null);

    [Fact]
    public void OpeningScansWhatIsOnScreen()
    {
        Show("a.cs", "one", "two", "one");
        var model = Model();
        model.SetText("one");

        model.Open();

        Assert.Equal(2, model.Hits.Value.Count);
    }

    [Fact]
    public void ATypedQueryAnchorsAtTheTopOfTheViewport()
    {
        Show("a.cs", "one", "two", "one");
        _topLine = 2;
        var model = Model();
        model.Open();

        model.SetText("one");

        Assert.Equal(new FileLine(3), model.Hits.Value.At?.Line);
    }

    [Fact]
    public void SteppingWrapsAtBothEnds()
    {
        Show("a.cs", "one", "one");
        var model = Model();
        model.Open();
        model.SetText("one");

        model.Next();
        Assert.Equal(2, model.Hits.Value.Ordinal);
        model.Next();
        Assert.Equal(1, model.Hits.Value.Ordinal);
        model.Previous();
        Assert.Equal(2, model.Hits.Value.Ordinal);
    }

    [Fact]
    public void SteppingWithNothingFoundDoesNothing()
    {
        Show("a.cs", "one");
        var model = Model();
        model.Open();
        model.SetText("zebra");

        model.Next();

        Assert.Equal(0, model.Hits.Value.Ordinal);
    }

    // A preview republishes itself when its highlighting and outline land, seconds after its text.
    // Sending the reader back to the first hit on each of those would make the bar unusable on a
    // file that is still settling.
    [Fact]
    public void TheSameFileArrivingAgainLeavesTheReaderWhereTheyWereStanding()
    {
        Show("a.cs", "one", "one", "one");
        var model = Model();
        model.Open();
        model.SetText("one");
        model.Next();
        model.Next();

        Show("a.cs", "one", "one", "one");
        model.Retarget();

        Assert.Equal(new FileLine(3), model.Hits.Value.At?.Line);
    }

    [Fact]
    public void AnotherFileStartsAtTheTopOfIt()
    {
        Show("a.cs", "one", "one");
        var model = Model();
        model.Open();
        model.SetText("one");
        model.Next();

        Show("b.cs", "nothing here", "one");
        model.Retarget();

        Assert.Equal("b.cs", model.Hits.Value.Path);
        Assert.Equal(new FileLine(2), model.Hits.Value.At?.Line);
    }

    [Fact]
    public void ClosingDropsTheHitsAndKeepsTheQuery()
    {
        Show("a.cs", "one");
        var model = Model();
        model.Open();
        model.SetText("one");

        model.Close();

        Assert.Equal(0, model.Hits.Value.Count);
        Assert.Equal("one", model.Text.Value);

        model.Open();
        Assert.Equal(1, model.Hits.Value.Count);
    }

    [Fact]
    public void AskingForAnOpenBarAsksTheFieldForTheCaretBack()
    {
        Show("a.cs", "one");
        var model = Model();
        var refocused = 0;
        model.RefocusRequested += () => refocused++;

        model.Open();
        Assert.Equal(0, refocused);

        model.Open();
        Assert.Equal(1, refocused);
    }

    [Fact]
    public void TogglingAnOptionRescansWithIt()
    {
        Show("a.cs", "Total total");
        var model = Model();
        model.Open();
        model.SetText("total");
        Assert.Equal(2, model.Hits.Value.Count);

        model.ToggleMatchCase();

        Assert.Equal(1, model.Hits.Value.Count);
    }

    [Fact]
    public void APreviewThatIsNotTextFindsNothing()
    {
        _shown = new FilePreview.Unavailable("a.png", FilePreviewRefusal.Binary);
        var model = Model();

        model.Open();
        model.SetText("one");

        Assert.Equal(0, model.Hits.Value.Count);
    }
}

/// <summary>Find against a real file on disk, where "is there text on screen" is settled by what the
/// preview actually loaded rather than by a stub.</summary>
public class FileSearchPreviewTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-file-search-");
    private readonly QueuedDispatcher _dispatcher = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void AMarkdownFileOffersFindOnlyWhileItsSourceIsShowing()
    {
        using var browser = Show("notes.md", "# Title\n\nA paragraph about totals.\n");
        Assert.False(browser.CanSearch);

        browser.SetRenderMarkdown(false);

        Assert.True(browser.CanSearch);
    }

    // The rendered document has no lines to highlight, so a bar left open over it would stand there
    // counting hits in text the reader cannot see.
    [Fact]
    public void SwitchingBackToTheRenderedDocumentClosesTheBar()
    {
        using var browser = Show("notes.md", "# Title\n\nA paragraph about totals.\n");
        browser.SetRenderMarkdown(false);
        browser.Search.Open();
        browser.Search.SetText("paragraph");
        Assert.Equal(1, browser.Search.Hits.Value.Count);

        browser.SetRenderMarkdown(true);

        Assert.False(browser.Search.IsOpen.Value);
        Assert.Equal(0, browser.Search.Hits.Value.Count);
    }

    [Fact]
    public void ThePreviewedFileIsWhatGetsSearched()
    {
        using var browser = Show("sum.cs", "var total = 0;\nreturn total;\n");

        browser.Search.Open();
        browser.Search.SetText("total");

        Assert.Equal(2, browser.Search.Hits.Value.Count);
        Assert.EndsWith("sum.cs", browser.Search.Hits.Value.Path);
    }

    private FileBrowserViewModel Show(string name, string content)
    {
        File.WriteAllText(System.IO.Path.Combine(_dir.Path, name), content);

        var browser = new FileBrowserViewModel(
            new Repo(Guid.NewGuid(), _dir.Path, "repo"),
            new FileSystemReader(),
            NoIgnoreOracle.Instance,
            new UnparsedFiles(),
            _dispatcher,
            new FileBrowserUiState(),
            _ => { });

        browser.Invalidate();
        WaitFor(browser, () => browser.Rows.Value.Count > 0);
        browser.SetCursor(System.IO.Path.Combine(_dir.Path, name));
        WaitFor(browser, () => browser.Preview.Value is FilePreview.Text);
        return browser;
    }

    private void WaitFor(FileBrowserViewModel browser, Func<bool> until)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            browser.Pending.Wait(TimeSpan.FromSeconds(5));
            _dispatcher.Drain();
            if (until()) return;
            Thread.Sleep(5);
        }
        Assert.Fail("The browser never reached the expected state.");
    }
}
