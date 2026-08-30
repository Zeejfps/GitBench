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

        Assert.Null(store.Active.Value);
    }

    [Fact]
    public void ActivatingARepo_PublishesATerminalThatHasStartedNothing()
    {
        using var store = Store();

        _registry.SetActive(_first);

        var terminal = Assert.IsType<TerminalInstance>(store.Active.Value);
        Assert.IsType<TerminalRenderState.Idle>(terminal.Render.Value);
        Assert.Empty(_ptys);
    }

    [Fact]
    public void EachRepo_GetsItsOwnTerminal()
    {
        using var store = Store();

        _registry.SetActive(_first);
        var first = store.Active.Value;
        _registry.SetActive(_second);

        Assert.NotSame(first, store.Active.Value);
    }

    [Fact]
    public void SwitchingAwayAndBack_ReturnsTheSameShellStillRunning()
    {
        using var store = Store();
        _registry.SetActive(_first);
        var terminal = store.Active.Value!;
        StartShell(terminal);

        _registry.SetActive(_second);
        _registry.SetActive(_first);

        Assert.Same(terminal, store.Active.Value);
        Assert.IsType<TerminalRenderState.Running>(terminal.Render.Value);
        Assert.False(_ptys[_first].IsDisposed, "Switching repositories killed the shell it left.");
    }

    [Fact]
    public void ARepoLeftBehind_KeepsRunningWhileAnotherIsOnScreen()
    {
        using var store = Store();
        _registry.SetActive(_first);
        StartShell(store.Active.Value!);
        _registry.SetActive(_second);
        StartShell(store.Active.Value!);

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
        var terminal = store.Active.Value!;
        StartShell(terminal);

        _registry.RemoveRepo(_first);

        Assert.True(_ptys[_first].IsDisposed, "The removed repository's shell was left running.");
        Assert.NotSame(terminal, store.Active.Value);
    }

    [Fact]
    public void RemovingARepoThatIsNotOnScreen_EndsItsShellAnyway()
    {
        using var store = Store();
        _registry.SetActive(_first);
        StartShell(store.Active.Value!);
        _registry.SetActive(_second);

        _registry.RemoveRepo(_first);

        Assert.True(_ptys[_first].IsDisposed);
        Assert.NotNull(store.Active.Value);
    }

    [Fact]
    public void DisposingTheStore_EndsEveryShell()
    {
        // The only guarantee the feature makes about lifetime: a terminal lives exactly as long as
        // the application does.
        var store = Store();
        _registry.SetActive(_first);
        StartShell(store.Active.Value!);
        _registry.SetActive(_second);
        StartShell(store.Active.Value!);

        store.Dispose();

        Assert.True(_ptys[_first].IsDisposed);
        Assert.True(_ptys[_second].IsDisposed);
        Assert.Null(store.Active.Value);
    }

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
        return pty;
    }

    void StartShell(TerminalInstance terminal)
    {
        terminal.ReportViewport(Viewport);
        terminal.Start();
        Pump.WaitFor(
            _dispatcher,
            () => terminal.Render.Value is TerminalRenderState.Running,
            "the shell to be adopted");
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
