using GitBench.Features.FileBrowser;
using GitBench.Git;
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
            NoIgnoreOracle.Instance,
            new UnparsedFiles(),
            _dispatcher,
            new FileBrowserUiState(),
            _ => { });

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
