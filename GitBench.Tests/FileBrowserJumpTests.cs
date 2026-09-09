using GitBench.App;
using GitBench.Features.CodeIntel;
using GitBench.Features.FileBrowser;
using GitBench.Git;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

[Collection(nameof(CodeIntelCollection))]
public sealed class FileBrowserJumpTests(CodeIntelFixture fixture) : IDisposable
{
    private readonly TempDir _dir = new("gitbench-file-jump-");
    private readonly TempDir _elsewhere = new("gitbench-file-jump-outside-");
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
        _elsewhere.Dispose();
    }

    private static readonly string[] Token =
    [
        "namespace App;",
        "",
        "class TokenCache",
        "{",
        "    void Get(string key)",
        "    {",
        "        Store(key);",
        "    }",
        "}",
    ];

    [Fact]
    public void JumpingToAFileOpensEveryDirectoryAboveIt()
    {
        Write("Auth.cs", "class AuthService", "{", "}");
        Write("src/deep/Token.cs", Token);
        using var browser = Show("Auth.cs");
        Assert.DoesNotContain(browser.Rows.Value, r => r.Name == "Token.cs");

        browser.NavigateTo(At("src/deep/Token.cs"), 7);
        Settle(browser);

        Assert.Contains(browser.Rows.Value, r => r.Name == "Token.cs");
        Assert.True(Row(browser, "src").As<FileBrowserRow.Directory>().IsExpanded);
        Assert.True(Row(browser, "deep").As<FileBrowserRow.Directory>().IsExpanded);
    }

    [Fact]
    public void TheCursorWaitsForTheListingRatherThanMovingToARowThatIsNotThereYet()
    {
        Write("Auth.cs", "class AuthService", "{", "}");
        Write("src/deep/Token.cs", Token);
        using var browser = Show("Auth.cs");

        browser.NavigateTo(At("src/deep/Token.cs"), 7);

        Assert.Equal(At("Auth.cs"), browser.Cursor.Value);

        Settle(browser);

        Assert.Equal(At("src/deep/Token.cs"), browser.Cursor.Value);
    }

    [Fact]
    public void JumpingToAFileSelectsItAndRevealsTheLine()
    {
        Write("Auth.cs", "class AuthService", "{", "}");
        Write("src/deep/Token.cs", Token);
        using var browser = Show("Auth.cs");
        var revealed = new List<int>();
        browser.LineRevealRequested += revealed.Add;

        browser.NavigateTo(At("src/deep/Token.cs"), 7);
        Settle(browser, () => revealed.Count > 0);

        Assert.Equal([7], revealed);
        Assert.Equal(At("src/deep/Token.cs"), Assert.IsType<FilePreview.Text>(browser.Preview.Value).Path);
    }

    [Fact]
    public void AFileOutsideTheRepositoryIsShownWithNothingSelected()
    {
        Write("Auth.cs", "class AuthService", "{", "}");
        var outside = Outside("std/io.cs", "class Stream", "{", "}", "class Extra");
        using var browser = Show("Auth.cs");
        var revealed = new List<int>();
        browser.LineRevealRequested += revealed.Add;

        browser.NavigateTo(outside, 4);
        Settle(browser, () => revealed.Count > 0);

        Assert.Null(browser.Cursor.Value);
        Assert.Equal(outside, Assert.IsType<FilePreview.Text>(browser.Preview.Value).Path);
        Assert.Equal([4], revealed);
    }

    [Fact]
    public void ADetachedPreviewIsTitledWithItsWholePath()
    {
        Write("Auth.cs", "class AuthService", "{", "}");
        var outside = Outside("std/io.cs", "class Stream", "{", "}");
        using var browser = Show("Auth.cs");

        browser.NavigateTo(outside, 1);
        Settle(browser);

        Assert.Equal(outside.Replace('\\', '/'), browser.TitleFor(browser.Preview.Value));
    }

    [Fact]
    public void AFileInTheRepositoryIsTitledRelativeToIt()
    {
        Write("src/deep/Token.cs", Token);
        using var browser = Browser();
        browser.Invalidate();
        Settle(browser, () => browser.Rows.Value.Count > 0);

        browser.NavigateTo(At("src/deep/Token.cs"), 3);
        Settle(browser, () => browser.Preview.Value is FilePreview.Text);

        Assert.Equal("src/deep/Token.cs", browser.TitleFor(browser.Preview.Value));
    }

    [Fact]
    public void AFileTheTreeIsNotListingIsShownDetachedRatherThanNotAtAll()
    {
        Write("Auth.cs", "class AuthService", "{", "}");
        Write(".hidden/Token.cs", Token);
        using var browser = Show("Auth.cs");
        browser.SetShowHidden(false);
        Settle(browser);

        browser.NavigateTo(At(".hidden/Token.cs"), 7);
        Settle(browser, () => browser.Preview.Value is FilePreview.Text { Path: var p } && p != At("Auth.cs"));

        Assert.Null(browser.Cursor.Value);
        Assert.Equal(At(".hidden/Token.cs"), Assert.IsType<FilePreview.Text>(browser.Preview.Value).Path);
    }

    [Fact]
    public void WithNowhereToGoBackToGoingBackDoesNothing()
    {
        Write("Auth.cs", "class AuthService", "{", "}");
        using var browser = Show("Auth.cs");

        Assert.False(Nav.CanGoBack.Value);
        Nav.GoBack();
        Settle(browser);

        Assert.Equal(At("Auth.cs"), browser.Cursor.Value);
    }

    // Selecting a file opens it, and anything that opens a file is somewhere the reader has been.
    [Fact]
    public void MovingTheCursorOntoAnotherFileIsSomethingToComeBackFrom()
    {
        Write("Auth.cs", "class AuthService", "{", "}");
        Write("Other.cs", "class Other", "{", "}");
        using var browser = Show("Auth.cs");

        browser.SetCursor(At("Other.cs"));
        Settle(browser);

        Assert.True(Nav.CanGoBack.Value);
    }

    // A directory opens nothing, so it is not a place — the file beside it is still on screen.
    [Fact]
    public void MovingTheCursorOntoADirectoryIsNot()
    {
        Write("Auth.cs", "class AuthService", "{", "}");
        Write("src/deep/Token.cs", Token);
        using var browser = Show("Auth.cs");

        browser.SetCursor(At("src"));
        Settle(browser);

        Assert.False(Nav.CanGoBack.Value);
        Assert.Equal(At("Auth.cs"), (browser.Preview.Value as FilePreview.Text)?.Path);
    }

    [Fact]
    public void GoingBackReturnsToTheFileTheJumpLeft()
    {
        Write("Auth.cs", "class AuthService", "{", "}");
        Write("src/deep/Token.cs", Token);
        using var browser = Show("Auth.cs");

        browser.NavigateTo(At("src/deep/Token.cs"), 7);
        Settle(browser, () => browser.Cursor.Value == At("src/deep/Token.cs"));
        Assert.True(Nav.CanGoBack.Value);

        Nav.GoBack();
        Settle(browser, () => browser.Cursor.Value == At("Auth.cs"));

        Assert.Equal(At("Auth.cs"), browser.Cursor.Value);
        Assert.False(Nav.CanGoBack.Value);
    }

    [Fact]
    public void GoingBackReturnsToTheLineTheReaderWasOn()
    {
        Write("Auth.cs", Token);
        Write("src/deep/Token.cs", Token);
        using var browser = Show("Auth.cs");
        browser.SetTopVisibleLine(6);

        browser.NavigateTo(At("src/deep/Token.cs"), 3);
        Settle(browser, () => browser.Preview.Value is FilePreview.Text { Path: var p }
            && p == At("src/deep/Token.cs"));

        var revealed = new List<int>();
        browser.LineRevealRequested += revealed.Add;
        Nav.GoBack();
        Settle(browser, () => revealed.Count > 0);

        Assert.Equal([6], revealed);
    }

    [Fact]
    public void GoingBackFromADetachedPreviewSelectsTheRowAgain()
    {
        Write("Auth.cs", "class AuthService", "{", "}");
        var outside = Outside("std/io.cs", "class Stream", "{", "}");
        using var browser = Show("Auth.cs");

        browser.NavigateTo(outside, 1);
        Settle(browser, () => browser.Preview.Value is FilePreview.Text { Path: var p } && p == outside);

        Nav.GoBack();
        Settle(browser, () => browser.Preview.Value is FilePreview.Text { Path: var p } && p == At("Auth.cs"));

        Assert.Equal(At("Auth.cs"), browser.Cursor.Value);
        Assert.Equal(At("Auth.cs"), Assert.IsType<FilePreview.Text>(browser.Preview.Value).Path);
    }

    [Fact]
    public void JumpsAreWalkedBackOneAtATimeInTheOrderTheyWereMade()
    {
        Write("Auth.cs", "class AuthService", "{", "}");
        Write("src/deep/Token.cs", Token);
        Write("src/Store.cs", "class Store", "{", "}");
        using var browser = Show("Auth.cs");

        browser.NavigateTo(At("src/deep/Token.cs"), 7);
        Settle(browser, () => browser.Cursor.Value == At("src/deep/Token.cs"));
        browser.NavigateTo(At("src/Store.cs"), 2);
        Settle(browser, () => browser.Cursor.Value == At("src/Store.cs"));

        Nav.GoBack();
        Settle(browser, () => browser.Cursor.Value == At("src/deep/Token.cs"));
        Assert.Equal(At("src/deep/Token.cs"), browser.Cursor.Value);

        Nav.GoBack();
        Settle(browser, () => browser.Cursor.Value == At("Auth.cs"));
        Assert.Equal(At("Auth.cs"), browser.Cursor.Value);
        Assert.False(Nav.CanGoBack.Value);
    }

    [Fact]
    public void AJumpWithinOneFileIsStillSomethingToComeBackFrom()
    {
        Write("Auth.cs", Token);
        using var browser = Show("Auth.cs");
        browser.SetTopVisibleLine(2);

        var revealed = new List<int>();
        browser.LineRevealRequested += revealed.Add;
        browser.NavigateTo(At("Auth.cs"), 7);
        Settle(browser, () => revealed.Count > 0);
        Assert.Equal([7], revealed);

        Nav.GoBack();
        Settle(browser, () => revealed.Count > 1);

        Assert.Equal([7, 2], revealed);
    }

    [Fact]
    public void AJumpWhoseListingFailsStillLandsSomewhere()
    {
        Write("Auth.cs", "class AuthService", "{", "}");
        Write("src/deep/Token.cs", Token);
        using var browser = Show("Auth.cs");
        var revealed = new List<int>();
        browser.LineRevealRequested += revealed.Add;
        _reader.Broken = true;

        browser.NavigateTo(At("src/deep/Token.cs"), 5);
        Settle(browser, () => revealed.Count > 0);

        Assert.Equal([5], revealed);
        Assert.Equal(At("src/deep/Token.cs"), (browser.Preview.Value as FilePreview.Text)?.Path);
    }

    private sealed class FlakyReader(IFileSystemReader inner) : IFileSystemReader
    {
        public bool Broken { get; set; }

        public DirectoryListing List(string absoluteDirectory, CancellationToken cancellation) =>
            Broken
                ? throw new IOException("the disk went away mid-jump")
                : inner.List(absoluteDirectory, cancellation);

        public string? ResolveLinkTarget(string absolutePath) => inner.ResolveLinkTarget(absolutePath);
    }

    private string At(string relative) =>
        Path.Combine(_dir.Path, relative.Replace('/', Path.DirectorySeparatorChar));

    private string Outside(string relative, params string[] lines)
    {
        var path = Path.Combine(_elsewhere.Path, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
        return path;
    }

    private void Write(string relative, params string[] lines)
    {
        var path = At(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }

    private readonly FlakyReader _reader = new(new FileSystemReader());

    private FileBrowserViewModel Browser() => new(
        new Repo(Guid.NewGuid(), _dir.Path, "repo"),
        _reader,
        NoIgnoreOracle.Instance,
        fixture.Extractor,
        _dispatcher,
        new FileBrowserUiState(),
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
        Settle(browser, () => browser.Preview.Value is FilePreview.Text);

        // After the first file is open, so the trail these tests walk starts empty: a panel handed
        // a repository with nothing open drops off the files tab, which would be a step of its own.
        _store = new OneBrowser(browser);
        _navigator = new ContentNavigator(_store, new NoTerminals(), _mode);
        _navigator.Start();
        return browser;
    }

    private static FileBrowserRow Row(FileBrowserViewModel browser, string name) =>
        browser.Rows.Value.Single(r => r.Name == name);

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

internal static class RowAssertions
{
    public static T As<T>(this FileBrowserRow row) where T : FileBrowserRow => Assert.IsType<T>(row);
}
