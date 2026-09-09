using GitBench.Features.Repos;
using GitBench.Features.Terminal;
using GitBench.Git;
using GitBench.Pty;
using GitBench.Terminal.Vt;
using GitBench.Terminal.Vt.Adapters;
using ZGF.Gui;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests.Terminal;

/// <summary>
/// One terminal per repository, for as long as the application is running: which terminal the pane
/// is handed, that the others keep running behind it, and what ends them.
/// </summary>
public class TerminalSessionStoreTests : IDisposable
{
    static readonly TerminalSize Viewport = new(100, 37);

    readonly TempDir _dir = new("gitbench-terminal-store-");
    readonly RepoRegistry _registry;
    readonly QueuedDispatcher _dispatcher = new();
    readonly Dictionary<Guid, LifecyclePty> _ptys = new();

    // Every pseudo-terminal ever started, since a repository now has several terminals and the
    // dictionary above only remembers the last one each repository opened.
    readonly List<LifecyclePty> _allPtys = new();
    readonly Guid _first;
    readonly Guid _second;

    public TerminalSessionStoreTests()
    {
        var statePath = Path.Combine(_dir.Path, "state.json");
        _registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
        _first = OpenRepo("first");
        _second = OpenRepo("second");
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void WithNoActiveRepo_ThereIsNoTerminal()
    {
        // Its own registry: opening a repository activates it, so the fixture's two never leave the
        // store with nothing to show.
        var statePath = Path.Combine(_dir.Path, "empty.json");
        var empty = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
        using var store = new TerminalSessionStore(
            empty,
            new UnusedPtySessionFactory(),
            new XtermSharpEngineFactory(),
            _dispatcher,
            StubLaunch);
        store.Start();

        Assert.Null(ActiveTerminal(store));
    }

    [Fact]
    public void ActivatingARepo_StartsNothing()
    {
        // A repository is not handed a terminal it never asked for. Activating one publishes its
        // (empty) tabs, and nothing runs until something presses for a shell.
        using var store = Store();

        _registry.SetActive(_first);

        Assert.NotNull(store.Tabs.Value);
        Assert.Null(ActiveTerminal(store));
        Assert.Empty(_ptys);
    }

    [Fact]
    public void EachRepo_GetsItsOwnTerminals()
    {
        using var store = Store();

        _registry.SetActive(_first);
        var first = StartShell(store);
        _registry.SetActive(_second);
        StartShell(store);

        Assert.NotSame(first, ActiveTerminal(store));
    }

    [Fact]
    public void SwitchingAwayAndBack_ReturnsTheSameShellStillRunning()
    {
        using var store = Store();
        _registry.SetActive(_first);
        var terminal = StartShell(store);

        _registry.SetActive(_second);
        _registry.SetActive(_first);

        Assert.Same(terminal, ActiveTerminal(store));
        Assert.IsType<TerminalRenderState.Running>(terminal.Render.Value);
        Assert.False(_ptys[_first].IsDisposed, "Switching repositories killed the shell it left.");
    }

    [Fact]
    public void ARepoLeftBehind_KeepsRunningWhileAnotherIsOnScreen()
    {
        using var store = Store();
        _registry.SetActive(_first);
        StartShell(store);
        _registry.SetActive(_second);
        StartShell(store);

        Assert.False(_ptys[_first].IsDisposed);
        Assert.False(_ptys[_second].IsDisposed);
    }

    [Fact]
    public void RemovingARepo_EndsItsShellAndTakesItOffTheScreen()
    {
        // Not only the user's doing: worktree and submodule reconciliation removes rows nobody
        // touched, and a shell whose working directory has gone has nowhere to be.
        using var store = Store();
        _registry.SetActive(_first);
        var terminal = StartShell(store);

        _registry.RemoveRepo(_first);

        Assert.True(_ptys[_first].IsDisposed, "The removed repository's shell was left running.");
        Assert.NotSame(terminal, ActiveTerminal(store));
    }

    [Fact]
    public void RemovingARepoThatIsNotOnScreen_EndsItsShellAnyway()
    {
        using var store = Store();
        _registry.SetActive(_first);
        StartShell(store);
        _registry.SetActive(_second);
        StartShell(store);

        _registry.RemoveRepo(_first);

        Assert.True(_ptys[_first].IsDisposed);
        Assert.NotNull(ActiveTerminal(store));
    }

    [Fact]
    public void DisposingTheStore_EndsEveryShell()
    {
        // The only guarantee the feature makes about lifetime: a terminal lives exactly as long as
        // the application does.
        var store = Store();
        _registry.SetActive(_first);
        StartShell(store);
        _registry.SetActive(_second);
        StartShell(store);

        store.Dispose();

        Assert.True(_ptys[_first].IsDisposed);
        Assert.True(_ptys[_second].IsDisposed);
        Assert.Null(ActiveTerminal(store));
    }

    [Fact]
    public void WithNothingStarted_NoRepositoryIsHoldingAShell()
    {
        using var store = Store();
        _registry.SetActive(_first);
        _registry.SetActive(_second);

        // Both repositories have tabs — activating one makes them — and neither has a terminal in
        // them, let alone a process.
        Assert.Empty(store.ReposWithLiveShells());
        Assert.False(store.HasLiveShell(_first));
    }

    [Fact]
    public void ARepositoryWithAShell_IsListedAndTheOthersAreNot()
    {
        using var store = Store();
        _registry.SetActive(_first);
        StartShell(store);
        _registry.SetActive(_second);

        Assert.Equal(new[] { _first }, store.ReposWithLiveShells());
        Assert.True(store.HasLiveShell(_first));
        Assert.False(store.HasLiveShell(_second));
    }

    [Fact]
    public void ShellsInSeveralRepositories_AreAllListed()
    {
        using var store = Store();
        _registry.SetActive(_first);
        StartShell(store);
        _registry.SetActive(_second);
        StartShell(store);

        Assert.Equal(
            new HashSet<Guid> { _first, _second },
            store.ReposWithLiveShells().ToHashSet());
    }

    [Fact]
    public void AShellThatHasExited_DropsOutOfTheList()
    {
        using var store = Store();
        _registry.SetActive(_first);
        var terminal = StartShell(store);

        _ptys[_first].ShellExits();
        Pump.WaitFor(
            _dispatcher, () => terminal.Render.Value is TerminalRenderState.Exited, "the exit");

        Assert.Empty(store.ReposWithLiveShells());
    }

    [Fact]
    public void ARepositoryNeverActivated_IsNotHoldingAShell()
    {
        // No terminal was ever made for it, so the question has to answer rather than throw.
        using var store = Store();

        Assert.False(store.HasLiveShell(Guid.NewGuid()));
    }

    [Fact]
    public void SwitchingAwayAndBack_ComesBackToTheTabsThatWereOpen()
    {
        using var store = Store();
        _registry.SetActive(_first);
        var tabs = store.Tabs.Value!;
        StartShell(store);
        var second = StartShell(store);

        _registry.SetActive(_second);
        _registry.SetActive(_first);

        Assert.Same(tabs, store.Tabs.Value);
        Assert.Equal(2, store.Tabs.Value!.Terminals.Count);
        Assert.Same(second, ActiveTerminal(store));
    }

    [Fact]
    public void AShellInATabThatIsNotOnScreen_StillNamesItsRepository()
    {
        // The quit confirmation asks per repository over the whole list, so a live shell one tab
        // behind the one on screen is still something closing would end.
        using var store = Store();
        _registry.SetActive(_first);
        StartShell(store);
        store.Tabs.Value!.StartNew();

        Assert.True(store.HasLiveShell(_first));
        Assert.Equal(new[] { _first }, store.ReposWithLiveShells());
    }

    [Fact]
    public void RemovingARepo_EndsEveryOneOfItsShells()
    {
        using var store = Store();
        _registry.SetActive(_first);
        var tabs = store.Tabs.Value!;
        StartShell(store);
        StartShell(store);

        _registry.RemoveRepo(_first);

        Assert.All(_allPtys, pty => Assert.True(pty.IsDisposed, "A removed repository left a shell running."));
        Assert.False(tabs.HasLiveShell);
        Assert.NotSame(tabs, store.Tabs.Value);
    }

    /// <summary>The terminal the pane would be drawing: the active repository's active tab.</summary>
    static TerminalInstance? ActiveTerminal(TerminalSessionStore store) => store.Tabs.Value?.Active.Value;

    TerminalSessionStore Store()
    {
        var store = new TerminalSessionStore(
            _registry,
            new UnusedPtySessionFactory(),
            new XtermSharpEngineFactory(),
            _dispatcher,
            StubLaunch);
        store.Start();
        return store;
    }

    ITerminalLaunch StubLaunch(Repo repo) => new StoreLaunch(() => PtyFor(repo.Id));

    LifecyclePty PtyFor(Guid repoId)
    {
        var pty = new LifecyclePty();
        _ptys[repoId] = pty;
        _allPtys.Add(pty);
        return pty;
    }

    /// <summary>Asks the active repository for a terminal and lets its grid report a size, which is
    /// what the spawn is waiting on. Returns the terminal it made.</summary>
    TerminalInstance StartShell(TerminalSessionStore store)
    {
        var terminal = store.Tabs.Value!.StartNew();
        terminal.ReportViewport(Viewport);
        Pump.WaitFor(
            _dispatcher,
            () => terminal.Render.Value is TerminalRenderState.Running,
            "the shell to be adopted");
        return terminal;
    }

    Guid OpenRepo(string name)
    {
        var path = Path.Combine(_dir.Path, name);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        _registry.Open(path);
        return _registry.Repos.Single(r => r.Path == path).Id;
    }

    sealed class StoreLaunch : ITerminalLaunch
    {
        readonly Func<IPtySession> _open;

        public StoreLaunch(Func<IPtySession> open) => _open = open;

        public string Name => "shell";

        public TerminalSize SizeFor(TerminalSize viewport) => viewport;

        public TerminalSession Start(TerminalSize size, IUiDispatcher dispatcher) =>
            TerminalSession.Start(_open, new XtermSharpEngineFactory(), size, dispatcher);
    }

    /// <summary>The store must never reach the real spawn path in a test.</summary>
    sealed class UnusedPtySessionFactory : IPtySessionFactory
    {
        public IPtySession Start(PtySessionOptions options) =>
            throw new InvalidOperationException("The store spawned a real shell.");
    }
}
