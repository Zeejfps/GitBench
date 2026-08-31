using GitBench.Features.CodeIntel;
using GitBench.Features.FileBrowser;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// Moving the reader within the file already on screen. The jump list is the first caller, so this
/// pins the one entry point rather than the control: a jump has to reach the body drawing the file,
/// and has to stay silent when there is no such body.
/// </summary>
[Collection(nameof(CodeIntelCollection))]
public class FileBrowserNavigationTests(CodeIntelFixture fixture) : IDisposable
{
    private readonly TempDir _dir = new("gitbench-file-nav-");
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly List<FileBrowserUiState> _persisted = [];

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void APreviewedSourceFileOffersItsDeclarations()
    {
        Write("Auth.cs", "namespace App;", "", "class AuthService", "{", "    void Login(string user) { }", "}");
        using var browser = Show("Auth.cs");

        var outline = Assert.IsType<FileOutline>(browser.Outline);
        var names = outline.Flatten().Select(n => n.Name).ToArray();

        Assert.Contains("AuthService", names);
        Assert.Contains("Login", names);
    }

    // Not every previewable file is code, and nothing downstream should have to ask twice.
    [Fact]
    public void APreviewedTextFileOffersNone()
    {
        Write("notes.txt", "just words");
        using var browser = Show("notes.txt");

        Assert.Null(browser.Outline);
    }

    // A jump is an occurrence, not a value: asking for the same line twice has to scroll twice,
    // which is what would be lost if this were published as state.
    [Fact]
    public void NavigatingToALineAsksTheBodyToRevealIt()
    {
        Write("Auth.cs", "class A", "{", "}");
        using var browser = Show("Auth.cs");

        var revealed = new List<int>();
        browser.LineRevealRequested += revealed.Add;
        browser.NavigateToLine(2);
        browser.NavigateToLine(2);

        Assert.Equal([2, 2], revealed);
    }

    [Fact]
    public void NavigatingWithNoFileOnScreenAsksForNothing()
    {
        using var browser = Browser();

        var revealed = new List<int>();
        browser.LineRevealRequested += revealed.Add;
        browser.NavigateToLine(2);

        Assert.Empty(revealed);
    }

    [Fact]
    public void ExpandingAFileListsItsDeclarationsIndentedInSourceOrder()
    {
        Write("Auth.cs",
            "namespace App;",
            "",
            "class AuthService",
            "{",
            "    void Login(string user) { }",
            "",
            "    void Login(string user, int attempt) { }",
            "}");
        using var browser = Show("Auth.cs");

        Expand(browser, "Auth.cs");

        Assert.Equal(
            [("Auth.cs", 0), ("App", 1), ("AuthService", 2), ("Login", 3), ("Login", 3)],
            browser.Rows.Value.Select(r => (r.Name, r.Depth)).ToArray());

        var overloads = browser.Rows.Value.OfType<FileBrowserRow.Symbol>()
            .Where(r => r.Name == "Login")
            .Select(r => r.ParameterTypes)
            .ToArray();
        Assert.Equal(["string", "string, int"], overloads);
    }

    // The chevron comes from the extension, so it is drawn before anything is read; only a language
    // the parser has a grammar for gets one.
    [Fact]
    public void OnlyAFileWithAGrammarOpens()
    {
        Write("Auth.cs", "class A", "{", "}");
        Write("notes.txt", "just words");
        using var browser = Show("Auth.cs");

        Assert.True(FileRow(browser, "Auth.cs").IsExpandable);
        Assert.False(FileRow(browser, "notes.txt").IsExpandable);
    }

    [Fact]
    public void CollapsingAFileTakesItsDeclarationsWithIt()
    {
        Write("Auth.cs", "class AuthService", "{", "}");
        using var browser = Show("Auth.cs");
        Expand(browser, "Auth.cs");
        Assert.Contains(browser.Rows.Value, r => r is FileBrowserRow.Symbol);

        Expand(browser, "Auth.cs");

        Assert.DoesNotContain(browser.Rows.Value, r => r is FileBrowserRow.Symbol);
    }

    [Fact]
    public void ClosingADeclarationHidesWhatItDeclares()
    {
        Write("Auth.cs",
            "class AuthService",
            "{",
            "    void Login(string user) { }",
            "}");
        using var browser = Show("Auth.cs");
        Expand(browser, "Auth.cs");
        Assert.Contains(browser.Rows.Value, r => r.Name == "Login");

        Collapse(browser, "AuthService");

        Assert.Contains(browser.Rows.Value, r => r.Name == "AuthService");
        Assert.DoesNotContain(browser.Rows.Value, r => r.Name == "Login");
    }

    // The chevron says "there is something in here", so a method that declares nothing has none.
    [Fact]
    public void ADeclarationWithNothingInsideItDoesNotOpen()
    {
        Write("Auth.cs",
            "class AuthService",
            "{",
            "    void Login(string user) { }",
            "}");
        using var browser = Show("Auth.cs");
        Expand(browser, "Auth.cs");

        Assert.True(Symbol(browser, "AuthService").IsExpandable);
        Assert.False(Symbol(browser, "Login").IsExpandable);
    }

    // Closed declarations are keyed by containment chain, not by line, so an edit above one leaves
    // it closed rather than springing it open on the next reconcile tick.
    [Fact]
    public void AClosedDeclarationSurvivesAnEditAboveIt()
    {
        Write("Auth.cs",
            "class AuthService",
            "{",
            "    void Login(string user) { }",
            "}");
        using var browser = Show("Auth.cs");
        Expand(browser, "Auth.cs");
        Collapse(browser, "AuthService");

        Write("Auth.cs",
            "// a line that was not there before",
            "",
            "class AuthService",
            "{",
            "    void Login(string user) { }",
            "}");
        browser.Invalidate();
        WaitFor(browser, () => Symbol(browser, "AuthService").StartLine == 3);

        Assert.False(Symbol(browser, "AuthService").IsExpanded);
        Assert.DoesNotContain(browser.Rows.Value, r => r.Name == "Login");
    }

    // A hidden line has no row, so a scroll to it would land on the fold that swallowed it. The
    // unfold has to happen first, and has to reach every ancestor: an outer fold hides an inner
    // one's body whatever the inner one says.
    [Fact]
    public void JumpingIntoAFoldedDeclarationOpensEveryFoldHidingIt()
    {
        Write("Auth.cs", Nested);
        using var browser = Show("Auth.cs");
        browser.ToggleFold("App.AuthService");
        browser.ToggleFold("App.AuthService.Login(string)");

        var revealed = new List<int>();
        browser.LineRevealRequested += revealed.Add;
        browser.NavigateToLine(7);

        Assert.Empty(browser.Folds.Value.Collapsed);
        Assert.Equal([7], revealed);
    }

    [Fact]
    public void JumpingToADeclarationThatIsStillOnScreenLeavesTheFoldsAlone()
    {
        Write("Auth.cs", Nested);
        using var browser = Show("Auth.cs");
        browser.ToggleFold("App.AuthService.Login(string)");

        // Line 5 is Login's own signature, which a collapsed Login still shows.
        browser.NavigateToLine(5);

        Assert.Equal(["App.AuthService.Login(string)"], browser.Folds.Value.Collapsed);
    }

    private static readonly string[] Nested =
    [
        "namespace App;",
        "",
        "class AuthService",
        "{",
        "    void Login(string user)",
        "    {",
        "        Check(user);",
        "    }",
        "}",
    ];

    [Fact]
    public void TheBreadcrumbNamesTheDeclarationAtTheTopOfTheViewport()
    {
        Write("Auth.cs",
            "namespace App;",
            "",
            "class AuthService",
            "{",
            "    void Login(string user)",
            "    {",
            "    }",
            "}");
        using var browser = Show("Auth.cs");

        browser.SetTopVisibleLine(6);

        Assert.Equal("AuthService.Login(string)", browser.Breadcrumb.Value);
    }

    // A namespace is the same for every declaration in the file, so it says nothing about where the
    // reader is; a line inside only the namespace is a line inside nothing worth naming.
    [Fact]
    public void TheBreadcrumbIsSilentInsideOnlyANamespace()
    {
        Write("Auth.cs",
            "namespace App;",
            "",
            "class AuthService",
            "{",
            "}");
        using var browser = Show("Auth.cs");

        browser.SetTopVisibleLine(2);

        Assert.Null(browser.Breadcrumb.Value);
    }

    [Fact]
    public void TheBreadcrumbClearsWhenAnotherFileIsPreviewed()
    {
        Write("Auth.cs",
            "class AuthService",
            "{",
            "    void Login(string user)",
            "    {",
            "    }",
            "}");
        Write("notes.txt", "just words");
        using var browser = Show("Auth.cs");
        browser.SetTopVisibleLine(4);
        Assert.NotNull(browser.Breadcrumb.Value);

        browser.SetCursor(Path.Combine(_dir.Path, "notes.txt"));

        Assert.Null(browser.Breadcrumb.Value);
    }

    [Fact]
    public void SelectingADeclarationRevealsWhereItStarts()
    {
        Write("Auth.cs",
            "class AuthService",
            "{",
            "    void Login(string user) { }",
            "}");
        using var browser = Show("Auth.cs");
        Expand(browser, "Auth.cs");

        var revealed = new List<int>();
        browser.LineRevealRequested += revealed.Add;
        browser.SelectSymbol(Symbol(browser, "Login"));

        Assert.Equal([3], revealed);
    }

    // The declaration's file is not the one on screen, so the reveal has to outlive the read that
    // brings it there — the case a plain "only if text is showing" rule silently drops.
    [Fact]
    public void SelectingADeclarationInAnotherFileWaitsForThatFileToLoad()
    {
        Write("Auth.cs", "class AuthService", "{", "}");
        Write("Token.cs",
            "class TokenCache",
            "{",
            "    void Get(string key) { }",
            "}");
        using var browser = Show("Auth.cs");
        Expand(browser, "Token.cs");

        var revealed = new List<int>();
        browser.LineRevealRequested += revealed.Add;
        browser.SelectSymbol(Symbol(browser, "Get"));
        Assert.Empty(revealed);

        WaitFor(browser, () => revealed.Count > 0);

        Assert.Equal([3], revealed);
        Assert.Equal(Path.Combine(_dir.Path, "Token.cs"), Assert.IsType<FilePreview.Text>(browser.Preview.Value).Path);
    }

    // A declaration's key is a file plus a line, which is not a path and must not be written to the
    // state file as one.
    [Fact]
    public void ACursorOnADeclarationIsNotPersisted()
    {
        Write("Auth.cs", "class AuthService", "{", "}");
        using var browser = Show("Auth.cs");
        Expand(browser, "Auth.cs");

        browser.SelectSymbol(Symbol(browser, "AuthService"));

        Assert.Null(_persisted[^1].Cursor);
    }

    private void Expand(FileBrowserViewModel browser, string name)
    {
        var before = FileRow(browser, name).IsExpanded;
        browser.ToggleFile(FileRow(browser, name));
        WaitFor(browser, () => FileRow(browser, name).IsExpanded != before);
    }

    private static FileBrowserRow.File FileRow(FileBrowserViewModel browser, string name) =>
        browser.Rows.Value.OfType<FileBrowserRow.File>().Single(r => r.Name == name);

    private void Collapse(FileBrowserViewModel browser, string name)
    {
        browser.ToggleSymbol(Symbol(browser, name));
        WaitFor(browser, () => !Symbol(browser, name).IsExpanded);
    }

    private static FileBrowserRow.Symbol Symbol(FileBrowserViewModel browser, string name) =>
        browser.Rows.Value.OfType<FileBrowserRow.Symbol>().Single(r => r.Name == name);

    private void Write(string name, params string[] lines) =>
        File.WriteAllLines(Path.Combine(_dir.Path, name), lines);

    private FileBrowserViewModel Browser() => new(
        new Repo(Guid.NewGuid(), _dir.Path, "repo"),
        new FileSystemReader(),
        NoIgnoreOracle.Instance,
        fixture.Extractor,
        _dispatcher,
        new FileBrowserUiState(),
        _persisted.Add);

    private FileBrowserViewModel Show(string name)
    {
        var browser = Browser();
        browser.Invalidate();
        WaitFor(browser, () => browser.Rows.Value.Count > 0);
        browser.SetCursor(Path.Combine(_dir.Path, name));
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
