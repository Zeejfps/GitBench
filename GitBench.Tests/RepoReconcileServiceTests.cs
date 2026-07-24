using System.Diagnostics;
using GitBench.Features.Repos;
using GitBench.Messages;
using ZGF.Gui.Desktop;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

// The watcher can still miss things it never sees at all — an FSW that fails to attach, a network
// share, a change made while the app was closed. Deferring instead of dropping fixes the events we
// receive; nothing re-checks the ones we never do. This is the safety net: while the app is focused,
// assume periodically that something was missed and re-broadcast the ordinary channel messages every
// subscriber already handles idempotently.
public sealed class RepoReconcileServiceTests : IDisposable
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan Never = TimeSpan.FromSeconds(30);

    private readonly TempDir _dir = new("gitbench-reconcile-");
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly MessageBus _bus = new();
    private readonly GateTracker _gate = new();
    private readonly FakeForeground _foreground = new();
    private readonly ChannelRecorder _seen;
    private readonly RepoRegistry _registry;
    private RepoReconcileService? _service;

    public RepoReconcileServiceTests()
    {
        _seen = new ChannelRecorder(_bus);
        var statePath = Path.Combine(_dir.Path, "repos.json");
        _registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
    }

    [Fact]
    public void Regaining_focus_reconciles_the_active_repo()
    {
        var repoId = OpenRepo("alpha");
        Start(Never);

        _foreground.Set(true);

        Pump.WaitFor(
            _dispatcher,
            () => _seen.WorkingTree >= 1 && _seen.Refs >= 1,
            "the focus-gain reconcile");
        Assert.Contains(repoId, _seen.WorkingTreeRepoIds);
        Assert.Contains(repoId, _seen.RefsRepoIds);
    }

    // Subscribing to a State fires immediately with the current value. Startup is the one moment
    // every store has just loaded, so treating that first fire as a focus gain would reconcile a
    // repo that was read milliseconds ago.
    [Fact]
    public void The_initial_focus_value_does_not_reconcile()
    {
        OpenRepo("alpha");
        _foreground.Set(true);

        Start(Never);

        Pump.DrainFor(_dispatcher, TimeSpan.FromMilliseconds(700));
        Assert.Equal(0, _seen.Total);
    }

    [Fact]
    public void A_focused_window_reconciles_on_every_tick()
    {
        OpenRepo("alpha");
        _foreground.Set(true);
        Start(Fast);

        Pump.WaitFor(_dispatcher, () => _seen.WorkingTree >= 3 && _seen.Refs >= 3, "three reconcile ticks");
    }

    [Fact]
    public void An_unfocused_window_does_not_tick()
    {
        OpenRepo("alpha");
        Start(Fast);

        Pump.DrainFor(_dispatcher, TimeSpan.FromSeconds(1.2));
        Assert.Equal(0, _seen.Total);
    }

    // A repo whose status read takes longer than the interval must not accumulate a queue of
    // reconciles behind it — each tick would add another pair of git reads to a disk already behind.
    [Fact]
    public void A_tick_is_skipped_while_git_is_still_reading_the_repo()
    {
        OpenRepo("alpha");
        _gate.Active = true;
        _foreground.Set(true);
        Start(Fast);

        Pump.DrainFor(_dispatcher, TimeSpan.FromSeconds(1.2));
        Assert.Equal(0, _seen.Total);

        _gate.Active = false;

        Pump.WaitFor(_dispatcher, () => _seen.WorkingTree >= 1 && _seen.Refs >= 1, "the reconcile once git went idle");
    }

    [Fact]
    public void No_active_repo_produces_no_broadcast()
    {
        _foreground.Set(true);
        Start(Fast);

        Pump.DrainFor(_dispatcher, TimeSpan.FromSeconds(1.2));
        Assert.Equal(0, _seen.Total);
    }

    [Fact]
    public void Losing_focus_stops_the_ticks()
    {
        OpenRepo("alpha");
        _foreground.Set(true);
        Start(Fast);
        Pump.WaitFor(_dispatcher, () => _seen.WorkingTree >= 1, "the first reconcile");

        _foreground.Set(false);
        Pump.DrainFor(_dispatcher, TimeSpan.FromMilliseconds(300));
        var settled = _seen.WorkingTree;

        Pump.DrainFor(_dispatcher, TimeSpan.FromSeconds(1.2));
        Assert.Equal(settled, _seen.WorkingTree);
    }

    [Fact]
    public void Disposal_stops_the_loop()
    {
        OpenRepo("alpha");
        _foreground.Set(true);
        Start(Fast);
        Pump.WaitFor(_dispatcher, () => _seen.WorkingTree >= 1, "the first reconcile");

        _service!.Dispose();
        Pump.DrainFor(_dispatcher, TimeSpan.FromMilliseconds(300));
        var settled = _seen.WorkingTree;

        Pump.DrainFor(_dispatcher, TimeSpan.FromSeconds(1.2));
        Assert.Equal(settled, _seen.WorkingTree);
    }

    // ---- helpers ----

    private void Start(TimeSpan interval)
    {
        _service = new RepoReconcileService(_registry, _bus, _dispatcher, _gate, _foreground, interval);
        _service.Start();
    }

    private Guid OpenRepo(string name)
    {
        var path = Path.Combine(_dir.Path, name);
        Directory.CreateDirectory(path);
        Git(path, "init", "-q", "-b", "main");
        Git(path, "config", "user.name", "Test");
        Git(path, "config", "user.email", "test@example.com");
        Git(path, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(path, "a.txt"), "0");
        Git(path, "add", "a.txt");
        Git(path, "commit", "-qm", "base");
        Assert.Equal(OpenRepoOutcome.Opened, _registry.Open(path));
        return _registry.Repos.Single(r => r.DisplayName == name).Id;
    }

    private static void Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({proc.ExitCode}): {stderr}");
    }

    public void Dispose()
    {
        _service?.Dispose();
        _registry.Dispose();
        _dir.Dispose();
    }

    private sealed class FakeForeground : IAppForeground
    {
        private readonly State<bool> _state = new(false);

        public bool Value => _state.Value;
        public IDisposable Subscribe(Action<bool> handler) => _state.Subscribe(handler);
        public void Set(bool foreground) => _state.Value = foreground;
    }
}
