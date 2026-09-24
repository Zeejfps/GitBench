using GitBench.App;
using GitBench.Features.CodeIntel;
using GitBench.Features.FileBrowser;
using GitBench.Features.Terminal;
using GitBench.Git;
using GitBench.Pty;
using GitBench.Terminal.Vt;
using ZGF.Gui;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// Switching repositories: each one comes back on the tab it was left on, and the switch itself is
/// never a step on either trail.
/// </summary>
[Collection(nameof(CodeIntelCollection))]
public sealed class ContentNavigatorRepoSwitchTests(CodeIntelFixture fixture) : IDisposable
{
    private readonly TempDir _dir = new("gitbench-content-switch-");
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly State<MainViewMode> _mode = new(MainViewMode.LocalChanges);
    private readonly Browsers _browsers = new();
    private readonly Terminals _terminals = new();
    private readonly List<(FileBrowserViewModel Browser, TerminalTabs Shells)> _repos = [];

    private ContentNavigator? _navigator;

    public void Dispose()
    {
        _navigator?.Dispose();
        foreach (var (browser, shells) in _repos)
        {
            shells.Dispose();
            browser.Dispose();
        }
        _dir.Dispose();
    }

    [Fact]
    public void EachRepository_ComesBackOnTheTabItWasLeftOn()
    {
        var a = NewRepo("a");
        var b = NewRepo("b");
        Start(a);

        Nav.Show(new ContentPlace.View(MainViewMode.History));
        SwitchTo(b);
        Nav.Show(new ContentPlace.View(MainViewMode.LocalChanges));

        SwitchTo(a);
        Assert.Equal(MainViewMode.History, _mode.Value);

        SwitchTo(b);
        Assert.Equal(MainViewMode.LocalChanges, _mode.Value);
    }

    [Fact]
    public void ARepositoryLeftOnItsTerminal_IsNotForgottenByTheOneWithout()
    {
        // The terminals switch before the files do, so the repository being left sees its terminal
        // tab vanish while it is still the active one. That is not the reader choosing to leave it.
        var a = NewRepo("a");
        var b = NewRepo("b");
        Start(a);
        var shell = a.Shells.StartNew();
        Nav.Show(new ContentPlace.Shell(shell));

        SwitchTo(b);
        Assert.Equal(MainViewMode.LocalChanges, _mode.Value);

        SwitchTo(a);
        Assert.Equal(MainViewMode.Terminal, _mode.Value);
        Assert.Same(shell, a.Shells.Active.Value);
    }

    [Fact]
    public void ATerminalClosedWhileAway_FallsBackToTheWorkingChanges()
    {
        var a = NewRepo("a");
        var b = NewRepo("b");
        Start(a);
        var shell = a.Shells.StartNew();
        Nav.Show(new ContentPlace.Shell(shell));
        SwitchTo(b);

        a.Shells.Close(shell);
        SwitchTo(a);

        Assert.Equal(MainViewMode.LocalChanges, _mode.Value);
    }

    [Fact]
    public void SwitchingBack_IsNotAStep()
    {
        var a = NewRepo("a");
        var b = NewRepo("b");
        Start(a);

        SwitchTo(b);
        Nav.Show(new ContentPlace.View(MainViewMode.History));
        SwitchTo(a);

        Assert.False(Nav.CanGoBack.Value);
    }

    [Fact]
    public void AFileShownInARepositoryNotOnScreen_LeavesTheOneOnScreenAlone()
    {
        var a = NewRepo("a");
        var b = NewRepo("b");
        Start(a);
        Nav.Show(new ContentPlace.View(MainViewMode.History));
        Nav.GoBack();

        _browsers.Show(b.Browser, new FileBrowserMove(null, IsMove: true));

        Assert.Equal(MainViewMode.LocalChanges, _mode.Value);
        Assert.False(Nav.CanGoBack.Value);
        Assert.True(Nav.CanGoForward.Value);
    }

    [Fact]
    public void ARepositoryAFileWasShownIn_WhileAway_ComesBackOnItsFiles()
    {
        var a = NewRepo("a");
        var b = NewRepo("b");
        Start(a);
        var file = Path.Combine(_dir.Path, "b", "stop.cs");
        File.WriteAllText(file, "class Stop {}\n");

        b.Browser.PlaceCaret(file, Features.Editor.TextPosition.At(1, 0));
        _browsers.Show(b.Browser, new FileBrowserMove(null, IsMove: true));
        SwitchTo(b);

        Assert.Equal(MainViewMode.Files, _mode.Value);
    }

    private ContentNavigator Nav => _navigator!;

    private (FileBrowserViewModel Browser, TerminalTabs Shells) NewRepo(string name)
    {
        var root = Path.Combine(_dir.Path, name);
        Directory.CreateDirectory(root);
        var browser = new FileBrowserViewModel(
            new Repo(Guid.NewGuid(), root, name),
            new FileSystemReader(),
            FileBrowserFakes.NoIgnore,
            FileBrowserFakes.EmptyCatalog,
            fixture.Extractor,
            fixture.Colors,
            _dispatcher,
            new FileBrowserUiState(),
            _ => { },
            TestDocuments.ForOneRepo(),
            TestDocuments.Discard,
            TestDocuments.KeepEdits);
        var shells = new TerminalTabs(() => new TerminalInstance(new NeverSpawns(), _dispatcher));
        _repos.Add((browser, shells));
        return (browser, shells);
    }

    private void Start((FileBrowserViewModel Browser, TerminalTabs Shells) repo)
    {
        SwitchTo(repo);
        _navigator = new ContentNavigator(_browsers, _terminals, _mode);
        _navigator.Start();
    }

    /// <summary>In the order the app's stores hear of it: the terminals are started first.</summary>
    private void SwitchTo((FileBrowserViewModel Browser, TerminalTabs Shells) repo)
    {
        _terminals.Active.Value = repo.Shells;
        _browsers.Active.Value = repo.Browser;
    }

    private sealed class Browsers : IFileBrowserStore
    {
        public State<FileBrowserViewModel?> Active { get; } = new(null);

        IReadable<FileBrowserViewModel?> IFileBrowserStore.Active => Active;

        public FileBrowserViewModel? For(Guid repoId) => null;

        public event Action<FileBrowserViewModel, FileBrowserMove>? FileShown;

        public void Show(FileBrowserViewModel browser, FileBrowserMove move) => FileShown?.Invoke(browser, move);

        public event Action? AllFilesClosed { add { } remove { } }
    }

    private sealed class Terminals : ITerminalSessionStore
    {
        public State<TerminalTabs?> Active { get; } = new(null);

        public IReadable<TerminalTabs?> Tabs => Active;

        public bool HasLiveShell(Guid repoId) => false;

        public IReadOnlyList<Guid> ReposWithLiveShells() => [];

        public TerminalInstance StartIn(Repo repo, ITerminalLaunch launch) => throw new NotSupportedException();

        public void CloseIn(Repo repo, TerminalInstance terminal) { }
    }

    private sealed class NeverSpawns : ITerminalLaunch
    {
        public string Name => "shell";

        public TerminalSize SizeFor(TerminalSize viewport) => viewport;

        public TerminalSession Start(TerminalSize size, IUiDispatcher dispatcher) =>
            throw new NotSupportedException("These tests never let a shell spawn.");
    }
}
