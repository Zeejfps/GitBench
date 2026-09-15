using GitBench.App;
using GitBench.Features.FileBrowser;
using GitBench.Git;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

[Collection(nameof(CodeIntelCollection))]
public sealed class FileBrowserTabsTests(CodeIntelFixture fixture) : IDisposable
{
    private readonly TempDir _dir = new("gitbench-file-tabs-");
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly List<FileBrowserUiState> _persisted = [];

    // Files, so that opening the first one is not itself a step away from the changes tab: these
    // are tests about the trail between files, not about the panel swinging onto them.
    private readonly State<MainViewMode> _mode = new(MainViewMode.Files);
    private OneBrowser? _store;
    private ContentNavigator? _navigator;

    /// <summary>The arrows, which walk the content panel's trail rather than the browser's.</summary>
    private ContentNavigator Nav => _navigator!;

    public void Dispose()
    {
        _navigator?.Dispose();
        _store?.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void LookingAtAFileOpensItInATabTheNextFileTakesBack()
    {
        Write("Auth.cs", "class AuthService", "{", "}");
        Write("Other.cs", "class Other", "{", "}");
        using var browser = Show("Auth.cs");

        Assert.Equal(["Auth.cs"], Names(browser));
        Assert.True(browser.Tabs[0].Transient.Value);

        browser.SetCursor(At("Other.cs"));
        Settle(browser, () => Preview(browser) == At("Other.cs"));

        Assert.Equal(["Other.cs"], Names(browser));
    }

    [Fact]
    public void OpeningAFileForGoodLeavesItsTabBehind()
    {
        Write("Auth.cs", "class AuthService", "{", "}");
        Write("Other.cs", "class Other", "{", "}");
        using var browser = Show("Auth.cs");

        browser.Activate(Row(browser, "Auth.cs"));
        Assert.False(browser.Tabs[0].Transient.Value);

        browser.SetCursor(At("Other.cs"));
        Settle(browser, () => Preview(browser) == At("Other.cs"));

        Assert.Equal(["Auth.cs", "Other.cs"], Names(browser));
    }

    [Fact]
    public void AJumpLeavesATabBehindAndTheBorrowedOneIsStillBorrowed()
    {
        Write("Auth.cs", "class AuthService", "{", "}");
        Write("Token.cs", "class TokenCache", "{", "}");
        Write("Store.cs", "class Store", "{", "}");
        using var browser = Show("Auth.cs");

        browser.NavigateTo(At("Token.cs"), 1);
        Settle(browser, () => Preview(browser) == At("Token.cs"));
        Assert.Equal(["Auth.cs", "Token.cs"], Names(browser));

        browser.SetCursor(At("Store.cs"));
        Settle(browser, () => Preview(browser) == At("Store.cs"));

        Assert.Equal(["Store.cs", "Token.cs"], Names(browser));
    }

    [Fact]
    public void ActivatingATabShowsItsFile()
    {
        Write("Auth.cs", "class AuthService", "{", "}");
        Write("Token.cs", "class TokenCache", "{", "}");
        using var browser = Show("Auth.cs");
        browser.NavigateTo(At("Token.cs"), 1);
        Settle(browser, () => Preview(browser) == At("Token.cs"));

        browser.ActivateTab(browser.Tabs[0]);
        Settle(browser, () => Preview(browser) == At("Auth.cs"));

        Assert.Equal(At("Auth.cs"), browser.Cursor.Value);
    }

    [Fact]
    public void ClosingTheActiveTabShowsTheOneBesideIt()
    {
        Write("Auth.cs", "class AuthService", "{", "}");
        Write("Token.cs", "class TokenCache", "{", "}");
        using var browser = Show("Auth.cs");
        browser.Activate(Row(browser, "Auth.cs"));
        browser.NavigateTo(At("Token.cs"), 1);
        Settle(browser, () => Preview(browser) == At("Token.cs"));

        browser.CloseTab(browser.Tabs[1]);
        Settle(browser, () => Preview(browser) == At("Auth.cs"));

        Assert.Equal(["Auth.cs"], Names(browser));
    }

    [Fact]
    public void ClosingTheLastTabLeavesNothingOnScreen()
    {
        Write("Auth.cs", "class AuthService", "{", "}");
        using var browser = Show("Auth.cs");

        browser.CloseTab(browser.Tabs[0]);
        Settle(browser);

        Assert.Empty(browser.Tabs);
        Assert.IsType<FilePreview.None>(browser.Preview.Value);
    }

    [Fact]
    public void ClosingTheOthersKeepsTheOneAskedFor()
    {
        Write("Auth.cs", "class AuthService", "{", "}");
        Write("Token.cs", "class TokenCache", "{", "}");
        Write("Store.cs", "class Store", "{", "}");
        using var browser = Show("Auth.cs");
        browser.NavigateTo(At("Token.cs"), 1);
        Settle(browser, () => Preview(browser) == At("Token.cs"));
        browser.NavigateTo(At("Store.cs"), 1);
        Settle(browser, () => Preview(browser) == At("Store.cs"));

        browser.CloseOtherTabs(browser.Tabs[1]);
        Settle(browser, () => Preview(browser) == At("Token.cs"));

        Assert.Equal(["Token.cs"], Names(browser));
    }

    [Fact]
    public void OpeningAnotherFileIsSomethingToComeBackFromAndGoForwardTo()
    {
        Write("Auth.cs", "class AuthService", "{", "}");
        Write("Other.cs", "class Other", "{", "}");
        using var browser = Show("Auth.cs");
        Assert.False(Nav.CanGoBack.Value);

        browser.SetCursor(At("Other.cs"));
        Settle(browser, () => Preview(browser) == At("Other.cs"));
        Assert.True(Nav.CanGoBack.Value);
        Assert.False(Nav.CanGoForward.Value);

        Nav.GoBack();
        Settle(browser, () => Preview(browser) == At("Auth.cs"));
        Assert.True(Nav.CanGoForward.Value);

        Nav.GoForward();
        Settle(browser, () => Preview(browser) == At("Other.cs"));

        Assert.Equal(At("Other.cs"), browser.Cursor.Value);
        Assert.False(Nav.CanGoForward.Value);
    }

    [Fact]
    public void GoingSomewhereNewDropsTheForwardTrail()
    {
        Write("Auth.cs", "class AuthService", "{", "}");
        Write("Other.cs", "class Other", "{", "}");
        Write("Third.cs", "class Third", "{", "}");
        using var browser = Show("Auth.cs");

        browser.SetCursor(At("Other.cs"));
        Settle(browser, () => Preview(browser) == At("Other.cs"));
        Nav.GoBack();
        Settle(browser, () => Preview(browser) == At("Auth.cs"));
        Assert.True(Nav.CanGoForward.Value);

        browser.SetCursor(At("Third.cs"));
        Settle(browser, () => Preview(browser) == At("Third.cs"));

        Assert.False(Nav.CanGoForward.Value);
    }

    [Fact]
    public void ComingBackToATabComesBackToTheLineItWasLeftOn()
    {
        Write("Auth.cs", "class A", "{", "}", "", "class B", "{", "}");
        Write("Other.cs", "class Other", "{", "}");
        using var browser = Show("Auth.cs");
        browser.Activate(Row(browser, "Auth.cs"));
        browser.SetTopVisibleLine(5);

        browser.SetCursor(At("Other.cs"));
        Settle(browser, () => Preview(browser) == At("Other.cs"));

        var revealed = new List<int>();
        browser.LineRevealRequested += revealed.Add;
        browser.ActivateTab(browser.Tabs[0]);
        Settle(browser, () => revealed.Count > 0);

        Assert.Equal([5], revealed);
    }

    [Fact]
    public void TheOpenTabsSurviveARestart()
    {
        Write("Auth.cs", "class AuthService", "{", "}");
        Write("Token.cs", "class TokenCache", "{", "}");
        using (var browser = Show("Auth.cs"))
        {
            browser.Activate(Row(browser, "Auth.cs"));
            browser.NavigateTo(At("Token.cs"), 1);
            Settle(browser, () => Preview(browser) == At("Token.cs"));
        }

        using var reopened = Browser(_persisted[^1]);
        reopened.Invalidate();
        Settle(reopened, () => Preview(reopened) == At("Token.cs"));

        Assert.Equal(["Auth.cs", "Token.cs"], Names(reopened));
        Assert.All(reopened.Tabs, tab => Assert.False(tab.Transient.Value));
    }

    [Fact]
    public void AClosedTabStaysClosedAcrossARestart()
    {
        Write("Auth.cs", "class AuthService", "{", "}");
        using (var browser = Show("Auth.cs"))
        {
            browser.Activate(Row(browser, "Auth.cs"));
            browser.CloseTab(browser.Tabs[0]);
            Settle(browser);
        }

        // The cursor is still on the closed file's row; that is not a tab.
        Assert.Empty(_persisted[^1].Tabs);
        Assert.Equal("Auth.cs", _persisted[^1].Cursor);

        using var reopened = Browser(_persisted[^1]);
        reopened.Invalidate();
        Settle(reopened, () => reopened.Rows.Value.Count > 0);

        Assert.Empty(reopened.Tabs);
        Assert.Equal(At("Auth.cs"), reopened.Cursor.Value);
    }

    [Fact]
    public void ATabOnAFileThatHasGoneIsNotReopened()
    {
        Write("Auth.cs", "class AuthService", "{", "}");
        Write("Token.cs", "class TokenCache", "{", "}");
        using (var browser = Show("Auth.cs"))
        {
            browser.Activate(Row(browser, "Auth.cs"));
            browser.NavigateTo(At("Token.cs"), 1);
            Settle(browser, () => Preview(browser) == At("Token.cs"));
        }

        File.Delete(At("Token.cs"));

        using var reopened = Browser(_persisted[^1]);
        reopened.Invalidate();
        Settle(reopened, () => Preview(reopened) == At("Auth.cs"));

        Assert.Equal(["Auth.cs"], Names(reopened));
    }

    private static IReadOnlyList<string> Names(FileBrowserViewModel browser) =>
        browser.Tabs.Select(tab => tab.Name).ToList();

    private static string? Preview(FileBrowserViewModel browser) =>
        (browser.Preview.Value as FilePreview.Text)?.Path;

    private static FileBrowserRow Row(FileBrowserViewModel browser, string name) =>
        browser.Rows.Value.Single(r => r.Name == name);

    private string At(string relative) =>
        Path.Combine(_dir.Path, relative.Replace('/', Path.DirectorySeparatorChar));

    private void Write(string relative, params string[] lines)
    {
        var path = At(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }

    private FileBrowserViewModel Browser(FileBrowserUiState? restored = null) => new(
        new Repo(Guid.NewGuid(), _dir.Path, "repo"),
        new FileSystemReader(),
        FileBrowserFakes.NoIgnore,
        FileBrowserFakes.EmptyCatalog,
        fixture.Extractor,
        _dispatcher,
        restored ?? new FileBrowserUiState(),
        _persisted.Add,
        TestDocuments.ForOneRepo(),
        TestDocuments.Discard,
            TestDocuments.KeepEdits);

    private FileBrowserViewModel Show(string relative)
    {
        var browser = Browser();
        browser.Invalidate();
        Settle(browser, () => browser.Rows.Value.Count > 0);
        browser.SetCursor(At(relative));
        Settle(browser, () => Preview(browser) == At(relative));

        // After the first file is open, so the trail these tests walk starts empty: a panel handed
        // a repository with nothing open drops off the files tab, which would be a step of its own.
        _store = new OneBrowser(browser);
        _navigator = new ContentNavigator(_store, new NoTerminals(), _mode);
        _navigator.Start();
        return browser;
    }

    private void Settle(FileBrowserViewModel browser, Func<bool>? until = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            browser.Pending.Wait(TimeSpan.FromSeconds(5));
            _dispatcher.Drain();
            if (until is null || until()) return;
            Thread.Sleep(5);
        }

        if (until is not null) Assert.Fail("The browser never reached the expected state.");
    }
}
