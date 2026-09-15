using GitBench.Features.Editor;
using GitBench.Features.FileBrowser;
using GitBench.Git;
using GitBench.Infrastructure;
using Xunit;

namespace GitBench.Tests;

/// <summary>What the tab strip owes a file with edits that are not on disk.</summary>
public class FileBrowserUnsavedTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-unsaved-");
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly RepoDocuments _documents = TestDocuments.ForOneRepo();
    private readonly List<IReadOnlyList<string>> _asked = [];
    private bool _answer = true;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void TypingIntoABorrowedTabKeepsTheNextFileFromTakingIt()
    {
        Write("one.txt", "one");
        Write("two.txt", "two");
        using var browser = Show("one.txt");
        Assert.True(browser.Tabs[0].Transient.Value);

        Edit("one.txt");
        browser.SetCursor(At("two.txt"));
        WaitFor(browser, () => browser.ActiveTab.Value?.Path == At("two.txt"));

        Assert.Equal(["one.txt", "two.txt"], Names(browser));
        Assert.False(browser.Tabs[0].Transient.Value);
    }

    [Fact]
    public void ABorrowedTabNobodyTypedIntoIsStillTakenBack()
    {
        Write("one.txt", "one");
        Write("two.txt", "two");
        using var browser = Show("one.txt");

        browser.SetCursor(At("two.txt"));
        WaitFor(browser, () => browser.ActiveTab.Value?.Path == At("two.txt"));

        Assert.Equal(["two.txt"], Names(browser));
    }

    [Fact]
    public void ClosingATabWithUnsavedEditsAsksFirstAndAbidesByNo()
    {
        Write("one.txt", "one");
        using var browser = Show("one.txt");
        Edit("one.txt");

        _answer = false;
        browser.CloseTab(browser.Tabs[0]);

        Assert.Equal([["one.txt"]], _asked);
        Assert.Equal(["one.txt"], Names(browser));
        Assert.True(_documents.HasUnsavedEdits(At("one.txt")));
    }

    [Fact]
    public void ClosingATabNobodyTypedIntoAsksNothing()
    {
        Write("one.txt", "one");
        using var browser = Show("one.txt");
        Open("one.txt");

        browser.CloseTab(browser.Tabs[0]);

        Assert.Empty(_asked);
        Assert.Empty(Names(browser));
    }

    [Fact]
    public void ClosingEveryTabAsksOnceForAllOfThem()
    {
        Write("one.txt", "one");
        Write("two.txt", "two");
        using var browser = Show("one.txt");
        Pin(browser, "one.txt");
        Edit("one.txt");
        Show(browser, "two.txt");
        Pin(browser, "two.txt");
        Edit("two.txt");

        browser.CloseAllTabs();

        Assert.Single(_asked);
        Assert.Equal(["one.txt", "two.txt"], _asked[0].Order());
        Assert.Empty(Names(browser));
    }

    [Fact]
    public void ClosingATabForgetsItsDocument()
    {
        Write("one.txt", "one");
        using var browser = Show("one.txt");
        Edit("one.txt");

        browser.CloseTab(browser.Tabs[0]);

        Assert.Empty(_documents.Unsaved());
    }

    private void Edit(string name) =>
        Open(name).Session.Type(SelectionRange.At(TextPosition.At(1, 0)), "x");

    private EditorBuffer Open(string name)
    {
        var path = At(name);
        var text = new FileText(File.ReadAllText(path));
        var writeBack = new FileWriteBack.Reversible(
            new FileEncoding(FileCharset.Utf8, LineEnding.Lf, EndsWithNewline: true));
        return _documents.Open(path, text, writeBack, null)!;
    }

    private static void Pin(FileBrowserViewModel browser, string name) =>
        browser.Tabs.Single(tab => tab.Name == name).Pin();

    private static IReadOnlyList<string> Names(FileBrowserViewModel browser) =>
        browser.Tabs.Select(tab => tab.Name).ToArray();

    private string At(string name) => PathKey.Normalize(Path.Combine(_dir.Path, name));

    private void Write(string name, string content) =>
        File.WriteAllText(Path.Combine(_dir.Path, name), content + "\n");

    private FileBrowserViewModel Show(string name)
    {
        var browser = new FileBrowserViewModel(
            new Repo(Guid.NewGuid(), _dir.Path, "repo"),
            new FileSystemReader(),
            FileBrowserFakes.NoIgnore,
            FileBrowserFakes.EmptyCatalog,
            new UnparsedFiles(),
            _dispatcher,
            new FileBrowserUiState(),
            _ => { },
            _documents,
            (files, close) => { _asked.Add(files); if (_answer) close(); },
            TestDocuments.KeepEdits);

        browser.Invalidate();
        WaitFor(browser, () => browser.Rows.Value.Count > 0);
        Show(browser, name);
        return browser;
    }

    private void Show(FileBrowserViewModel browser, string name)
    {
        browser.SetCursor(At(name));
        WaitFor(browser, () => browser.Preview.Value is FilePreview.Text text && text.Path == At(name));
    }

    private void WaitFor(FileBrowserViewModel browser, Func<bool> until)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            browser.Pending.GetAwaiter().GetResult();
            _dispatcher.Drain();
            if (until()) return;
            Thread.Sleep(5);
        }
        Assert.Fail("The browser never reached the expected state.");
    }
}
