using GitBench.Features.FileBrowser;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

[Collection(nameof(CodeIntelCollection))]
public sealed class FileBrowserTabsTests(CodeIntelFixture fixture) : IDisposable
{
    private readonly TempDir _dir = new("gitbench-file-tabs-");
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly List<FileBrowserUiState> _persisted = [];

    public void Dispose() => _dir.Dispose();

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
        Assert.False(browser.CanGoBack.Value);

        browser.SetCursor(At("Other.cs"));
        Settle(browser, () => Preview(browser) == At("Other.cs"));
        Assert.True(browser.CanGoBack.Value);
        Assert.False(browser.CanGoForward.Value);

        browser.GoBack();
        Settle(browser, () => Preview(browser) == At("Auth.cs"));
        Assert.True(browser.CanGoForward.Value);

        browser.GoForward();
        Settle(browser, () => Preview(browser) == At("Other.cs"));

        Assert.Equal(At("Other.cs"), browser.Cursor.Value);
        Assert.False(browser.CanGoForward.Value);
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
        browser.GoBack();
        Settle(browser, () => Preview(browser) == At("Auth.cs"));
        Assert.True(browser.CanGoForward.Value);

        browser.SetCursor(At("Third.cs"));
        Settle(browser, () => Preview(browser) == At("Third.cs"));

        Assert.False(browser.CanGoForward.Value);
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
        NoIgnoreOracle.Instance,
        fixture.Extractor,
        _dispatcher,
        restored ?? new FileBrowserUiState(),
        _persisted.Add);

    private FileBrowserViewModel Show(string relative)
    {
        var browser = Browser();
        browser.Invalidate();
        Settle(browser, () => browser.Rows.Value.Count > 0);
        browser.SetCursor(At(relative));
        Settle(browser, () => Preview(browser) == At(relative));
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
