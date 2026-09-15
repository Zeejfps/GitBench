using GitBench.Features.FileBrowser;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Platform;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// Making and removing entries from the browser's tree: what a name is allowed to be, and what the
/// tree does with a path once it exists or stops existing.
/// </summary>
[Collection(nameof(CodeIntelCollection))]
public class FileBrowserFileOpsTests(CodeIntelFixture fixture) : IDisposable
{
    private readonly TempDir _dir = new("gitbench-file-ops-");
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly List<FileBrowserUiState> _persisted = [];

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void APlainNameIsAccepted()
    {
        Assert.True(NewEntryRules.IsAcceptable("Auth.cs", _dir.Path));
        Assert.Null(NewEntryRules.Validate("Auth.cs", _dir.Path, Strings()));
    }

    // The one thing that saves this from being three dialogs to reach src/features/auth/Login.cs.
    [Fact]
    public void ANameMayNameTheFoldersAboveIt()
    {
        Assert.True(NewEntryRules.IsAcceptable("features/auth/Login.cs", _dir.Path));
        Assert.Equal(
            Path.Combine(_dir.Path, "features", "auth", "Login.cs"),
            NewEntryRules.Resolve("features/auth/Login.cs", _dir.Path));
    }

    [Fact]
    public void ANameThatIsTakenIsRefused()
    {
        Write("Auth.cs", "class A { }");
        Directory.CreateDirectory(Path.Combine(_dir.Path, "src"));

        Assert.False(NewEntryRules.IsAcceptable("Auth.cs", _dir.Path));
        Assert.False(NewEntryRules.IsAcceptable("src", _dir.Path));
        Assert.NotNull(NewEntryRules.Validate("Auth.cs", _dir.Path, Strings()));
    }

    [Theory]
    [InlineData("../escaped.cs")]
    [InlineData("a/../../escaped.cs")]
    [InlineData("./here.cs")]
    public void ANameMayNotStepOutOfTheFolder(string name)
    {
        Assert.False(NewEntryRules.IsAcceptable(name, _dir.Path));
        Assert.Null(NewEntryRules.Resolve(name, _dir.Path));
    }

    [Theory]
    [InlineData("a:b.cs")]
    [InlineData("what?.cs")]
    [InlineData("a|b.cs")]
    [InlineData("say \"hi\".cs")]
    public void ANameMayNotCarryAReservedCharacterOfEitherPlatform(string name) =>
        Assert.False(NewEntryRules.IsAcceptable(name, _dir.Path));

    // Nothing typed yet, and a path that stops at a separator, are incomplete rather than wrong:
    // the create button is off, but the field stays neutral while it is still being typed.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("src/")]
    public void AnUnfinishedNameIsGatedWithoutBeingCalledAnError(string name)
    {
        Assert.False(NewEntryRules.IsAcceptable(name, _dir.Path));
        Assert.Null(NewEntryRules.Validate(name, _dir.Path, Strings()));
    }

    [Fact]
    public void AFullPathIsRefused()
    {
        var rooted = Path.Combine(Path.GetTempPath(), "elsewhere.cs");
        Assert.False(NewEntryRules.IsAcceptable(rooted, _dir.Path));
    }

    [Fact]
    public void ANewFileIsListedOpenedAndUnderTheCursor()
    {
        Write("README.md", "# repo");
        using var browser = Listed();
        var path = Path.Combine(_dir.Path, "src", "Auth.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);

        browser.Created(path, isDirectory: false);
        WaitFor(browser, () => browser.Cursor.Value == path);

        Assert.Equal(path, browser.ActiveTab.Value?.Path);
        Assert.Contains(browser.Rows.Value, row => row.FullPath == path);
    }

    // A new directory has nothing in it to read, so it opens rather than being opened.
    [Fact]
    public void ANewFolderIsListedAndExpanded()
    {
        Write("README.md", "# repo");
        using var browser = Listed();
        var path = Path.Combine(_dir.Path, "features");
        Directory.CreateDirectory(path);

        browser.Created(path, isDirectory: true);
        WaitFor(browser, () => browser.Cursor.Value == path);

        var row = Assert.IsType<FileBrowserRow.Directory>(
            browser.Rows.Value.Single(r => r.FullPath == path));
        Assert.True(row.IsExpanded);
        Assert.Null(browser.ActiveTab.Value);
    }

    [Fact]
    public void ADeletedFileLeavesTheTreeAndTakesItsTabWithIt()
    {
        Write("Auth.cs", "class A { }");
        using var browser = Listed();
        var path = Path.Combine(_dir.Path, "Auth.cs");

        browser.SetCursor(path);
        WaitFor(browser, () => browser.ActiveTab.Value?.Path == path);

        File.Delete(path);
        browser.Deleted(path);
        WaitFor(browser, () => browser.Rows.Value.All(row => row.FullPath != path));

        Assert.Null(browser.ActiveTab.Value);
        Assert.Null(browser.Cursor.Value);
    }

    [Fact]
    public void ADeletedFolderClosesWhatWasOpenBeneathIt()
    {
        Directory.CreateDirectory(Path.Combine(_dir.Path, "src"));
        Write(Path.Combine("src", "Auth.cs"), "class A { }");
        using var browser = Listed();
        var folder = Path.Combine(_dir.Path, "src");
        var file = Path.Combine(folder, "Auth.cs");

        browser.NavigateTo(file, 1);
        WaitFor(browser, () => browser.ActiveTab.Value?.Path == file);

        DirectoryTree.Delete(folder);
        browser.Deleted(folder);
        WaitFor(browser, () => browser.Rows.Value.All(row => row.FullPath != folder));

        Assert.Null(browser.ActiveTab.Value);
    }

    // A sibling that happens to share the deleted folder's name as a prefix is not under it.
    [Fact]
    public void ADeletedFolderLeavesAPrefixSharingSiblingAlone()
    {
        Directory.CreateDirectory(Path.Combine(_dir.Path, "src"));
        Directory.CreateDirectory(Path.Combine(_dir.Path, "src-gen"));
        Write(Path.Combine("src-gen", "Auth.cs"), "class A { }");
        using var browser = Listed();
        var kept = Path.Combine(_dir.Path, "src-gen", "Auth.cs");

        browser.NavigateTo(kept, 1);
        WaitFor(browser, () => browser.ActiveTab.Value?.Path == kept);

        browser.Deleted(Path.Combine(_dir.Path, "src"));
        _dispatcher.Drain();

        Assert.Equal(kept, browser.ActiveTab.Value?.Path);
    }

    // The delete dialog asks the shell before it decides what it is promising the reader — a move
    // they can undo, or a file that is gone — so a shell that implements neither member has to
    // answer "no trash", and has to fail loudly rather than quietly doing nothing if asked anyway.
    [Fact]
    public void APlatformThatSaysNothingHasNoTrash()
    {
        IPlatformShell shell = new FakeShell();

        Assert.False(shell.CanMoveToTrash);
        Assert.Throws<NotSupportedException>(() => shell.MoveToTrash("/tmp/whatever"));
    }

    private static Strings Strings() => GitBench.Localization.Strings.For(Locale.En);

    private void Write(string name, string content) =>
        File.WriteAllText(Path.Combine(_dir.Path, name), content);

    private FileBrowserViewModel Listed()
    {
        var browser = new FileBrowserViewModel(
            new Repo(Guid.NewGuid(), _dir.Path, "repo"),
            new FileSystemReader(),
            FileBrowserFakes.NoIgnore,
            FileBrowserFakes.EmptyCatalog,
            fixture.Extractor,
            fixture.Colors,
            _dispatcher,
            new FileBrowserUiState(),
            _persisted.Add,
            TestDocuments.ForOneRepo(),
            TestDocuments.Discard,
            TestDocuments.KeepEdits);
        browser.Invalidate();
        WaitFor(browser, () => browser.Rows.Value.Count > 0);
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
