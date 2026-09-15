using GitBench.Features.Editor;
using GitBench.Features.Diff;
using GitBench.Features.FileBrowser;
using GitBench.Git;
using GitBench.Infrastructure;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// What a reconcile tick is allowed to do to the file already on screen. It arrives twice a minute
/// per active repository, and again on every editor save anywhere in the working tree, so anything
/// it republishes is something the reader loses their place in.
/// </summary>
public class FileBrowserPreviewRefreshTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-preview-refresh-");
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly List<FilePreview> _published = [];
    private readonly RepoDocuments _documents = TestDocuments.ForOneRepo();
    private readonly List<IReadOnlyList<string>> _reloadAsked = [];
    private Action? _reload;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void ARereadOfAnUnchangedFilePublishesNothing()
    {
        Write("notes.md", "# Title\n\nA paragraph.\n");
        using var browser = Show("notes.md");
        var shown = browser.Preview.Value;

        Watch(browser);
        browser.Invalidate();
        Quiet(browser);

        Assert.Empty(_published);
        Assert.Same(shown, browser.Preview.Value);
    }

    // The positive control for the test above: the same call on a file that did change must still
    // land, or "publishes nothing" would pass just as well on a preview that had stopped working.
    [Fact]
    public void ARereadOfAnEditedFilePublishesTheNewText()
    {
        Write("notes.md", "# Title\n\nA paragraph.\n");
        using var browser = Show("notes.md");

        Watch(browser);
        Write("notes.md", "# Title\n\nA different paragraph.\n");
        browser.Invalidate();
        WaitFor(browser, () => Lines(browser).Contains("A different paragraph."));

        Assert.Contains("# Title", Lines(browser));
        Assert.DoesNotContain(_published, p => p is FilePreview.Loading);
    }

    // Loading is what a reader should see when they ask for a file they are not already looking at.
    [Fact]
    public void MovingToAnotherFileStillShowsLoadingFirst()
    {
        Write("notes.md", "# Title\n");
        Write("other.md", "# Other\n");
        using var browser = Show("notes.md");

        Watch(browser);
        browser.SetCursor(Path.Combine(_dir.Path, "other.md"));
        WaitFor(browser, () => browser.Preview.Value is FilePreview.Text);

        Assert.Contains(_published, p => p is FilePreview.Loading);
    }

    [Fact]
    public void ATickDoesNotReplaceABufferBeingTypedInto()
    {
        Write("notes.md", "one");
        using var browser = Show("notes.md");
        var buffer = Editable(browser)!;
        buffer.Session.Type(SelectionRange.At(TextPosition.At(1, 0)), "x");

        Write("notes.md", "something else");
        browser.Invalidate();
        WaitFor(browser, () => Lines(browser).Contains("something else"));

        Assert.Same(buffer, Editable(browser));
        Assert.Equal("xone", buffer.Session.Document.Line(new FileLine(1)));
    }

    [Fact]
    public void ATickReplacesABufferNobodyTypedInto()
    {
        Write("notes.md", "one");
        using var browser = Show("notes.md");
        var buffer = Editable(browser)!;

        Write("notes.md", "something else");
        browser.Invalidate();
        WaitFor(browser, () => Lines(browser).Contains("something else"));

        var reread = Editable(browser)!;
        Assert.NotSame(buffer, reread);
        Assert.Equal("something else", reread.Session.Document.Line(new FileLine(1)));
    }

    [Fact]
    public void AnExternalChangeUnderADirtyBufferAsksBeforeAnythingIsLost()
    {
        Write("notes.md", "one");
        using var browser = Show("notes.md");
        var buffer = Editable(browser)!;
        buffer.Session.Type(SelectionRange.At(TextPosition.At(1, 0)), "x");

        Write("notes.md", "something else entirely");
        browser.Invalidate();
        WaitFor(browser, () => _reloadAsked.Count > 0);

        Assert.Equal(["notes.md"], _reloadAsked[0]);
        Assert.Same(buffer, Editable(browser));
        Assert.Equal("xone", buffer.Session.Document.Line(new FileLine(1)));
    }

    [Fact]
    public void TheSameExternalChangeIsOnlyAskedAboutOnce()
    {
        Write("notes.md", "one");
        using var browser = Show("notes.md");
        Editable(browser)!.Session.Type(SelectionRange.At(TextPosition.At(1, 0)), "x");

        Write("notes.md", "something else entirely");
        browser.Invalidate();
        WaitFor(browser, () => _reloadAsked.Count > 0);

        browser.Invalidate();
        Quiet(browser);

        Assert.Single(_reloadAsked);
    }

    [Fact]
    public void TakingTheFileOnDiskReplacesTheBuffer()
    {
        Write("notes.md", "one");
        using var browser = Show("notes.md");
        var buffer = Editable(browser)!;
        buffer.Session.Type(SelectionRange.At(TextPosition.At(1, 0)), "x");

        Write("notes.md", "something else entirely");
        browser.Invalidate();
        WaitFor(browser, () => _reload is not null);

        _reload!();
        WaitFor(browser, () => Lines(browser).Contains("something else entirely"));

        var reread = Editable(browser)!;
        Assert.NotSame(buffer, reread);
        Assert.Equal("something else entirely", reread.Session.Document.Line(new FileLine(1)));
    }

    [Fact]
    public void ACleanBufferReloadsWithoutAsking()
    {
        Write("notes.md", "one");
        using var browser = Show("notes.md");
        Editable(browser);

        Write("notes.md", "something else entirely");
        browser.Invalidate();
        WaitFor(browser, () => Lines(browser).Contains("something else entirely"));
        Quiet(browser);

        Assert.Empty(_reloadAsked);
    }

    [Fact]
    public void SavingDoesNotAskAboutTheFileJustSaved()
    {
        Write("notes.md", "one");
        using var browser = Show("notes.md");
        var buffer = Editable(browser)!;
        buffer.Session.Type(SelectionRange.At(TextPosition.At(1, 0)), "x");

        Save(browser, "notes.md");
        browser.Invalidate();
        Quiet(browser);

        Assert.Empty(_reloadAsked);
        Assert.False(_documents.HasUnsavedEdits(Path.Combine(_dir.Path, "notes.md")));
        Assert.Same(buffer, Editable(browser));
    }

    [Fact]
    public void TypingAgainRightAfterASaveStillDoesNotAsk()
    {
        Write("notes.md", "one");
        using var browser = Show("notes.md");
        var buffer = Editable(browser)!;
        buffer.Session.Type(SelectionRange.At(TextPosition.At(1, 0)), "x");
        Save(browser, "notes.md");

        buffer.Session.Type(SelectionRange.At(TextPosition.At(1, 0)), "y");
        browser.Invalidate();
        Quiet(browser);

        Assert.Empty(_reloadAsked);
        Assert.Equal("yxone", buffer.Session.Document.Line(new FileLine(1)));
    }

    [Fact]
    public void TheStagingFileOfASaveNeverAppearsInTheTree()
    {
        Write("notes.md", "one");
        Write("notes.md.4321-1.gitbench.tmp", "half a save");
        Write("notes.md.tmp", "a draft of the reader's own");
        Write("scratch.tmp", "a file of the reader's own");
        using var browser = Show("notes.md");

        browser.Invalidate();
        WaitFor(browser, () => Names(browser).Contains("scratch.tmp"));

        Assert.DoesNotContain("notes.md.4321-1.gitbench.tmp", Names(browser));
        Assert.Contains("notes.md.tmp", Names(browser));
        Assert.Contains("notes.md", Names(browser));
    }

    private static IReadOnlyList<string> Names(FileBrowserViewModel browser) =>
        browser.Rows.Value.OfType<FileBrowserRow.File>().Select(f => f.Name).ToArray();

    private void Save(FileBrowserViewModel browser, string name)
    {
        var path = Path.Combine(_dir.Path, name);
        File.WriteAllText(path, Editable(browser)!.Session.Document.Text);
        _documents.MarkSaved(path);
    }

    private EditorBuffer? Editable(FileBrowserViewModel browser) =>
        browser.Preview.Value is FilePreview.Text text
            ? _documents.Open(text.Path, text.Lines, text.WriteBack, text.Highlight)
            : null;

    private static IReadOnlyList<string> Lines(FileBrowserViewModel browser) =>
        browser.Preview.Value is FilePreview.Text text ? text.Lines : [];

    private void Write(string name, string content) =>
        File.WriteAllText(Path.Combine(_dir.Path, name), content);

    /// <summary>A browser with the file already previewed, which is the state every test starts from.</summary>
    private FileBrowserViewModel Show(string name)
    {
        var browser = new FileBrowserViewModel(
            new Repo(Guid.NewGuid(), _dir.Path, "repo"),
            new FileSystemReader(),
            FileBrowserFakes.NoIgnore,
            FileBrowserFakes.EmptyCatalog,
            new UnparsedFiles(),
            new PlainText(),
            _dispatcher,
            new FileBrowserUiState(),
            _ => { },
            _documents,
            TestDocuments.Discard,
            (files, reload) => { _reloadAsked.Add(files); _reload = reload; });

        browser.Invalidate();
        WaitFor(browser, () => browser.Rows.Value.Count > 0);
        browser.SetCursor(Path.Combine(_dir.Path, name));
        WaitFor(browser, () => browser.Preview.Value is FilePreview.Text);
        return browser;
    }

    private void Watch(FileBrowserViewModel browser)
    {
        _published.Clear();
        var first = true;
        browser.Preview.Subscribe(p =>
        {
            // Subscribe replays the current value; that is the state under test, not a publication.
            if (first) { first = false; return; }
            _published.Add(p);
        });
    }

    /// <summary>Runs the browser's threads until <paramref name="until"/> holds. The preview loads on
    /// a plain task rather than the tree's lane, so there is nothing to await — the dispatcher queue
    /// is the only place its result can appear.</summary>
    private void WaitFor(FileBrowserViewModel browser, Func<bool> until)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            Pump(browser);
            if (until()) return;
            Thread.Sleep(5);
        }
        Assert.Fail("The browser never reached the expected state.");
    }

    /// <summary>Runs the browser's threads for long enough that a publication would have landed.
    /// Reading one small file out of a temp directory is microseconds; this is orders of magnitude
    /// of headroom over that, so a quiet window here means the work chose not to publish.</summary>
    private void Quiet(FileBrowserViewModel browser)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        while (DateTime.UtcNow < deadline)
        {
            Pump(browser);
            Thread.Sleep(5);
        }
    }

    private void Pump(FileBrowserViewModel browser)
    {
        browser.Pending.Wait(TimeSpan.FromSeconds(5));
        _dispatcher.Drain();
    }
}
