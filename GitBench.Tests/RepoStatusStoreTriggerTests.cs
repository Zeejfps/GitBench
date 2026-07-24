using System.Collections.Concurrent;
using System.Diagnostics;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Messages;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

// IRepoStatusStore is the sole owner of the active repo's branch / ahead / behind / dirty signals —
// the toolbar's push-pull enablement, the status bar, and the branches sidebar badge all read that
// one slot. So a repo switch has to re-probe: without it every one of those shows the *previous*
// repo's numbers until an unrelated message happens to fire.
//
// Drives the real store over real throwaway repos, with the startup sweep deliberately never
// released — so the only thing that can produce a probe here is the active-repo trigger itself.
public sealed class RepoStatusStoreTriggerTests : IDisposable
{
    private readonly string _root;
    private readonly RepoRegistry _registry;
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly GitReadGate _gate = new();
    private readonly StartupSweepCoordinator _sweep;
    private readonly RepoStatusStore _store;

    public RepoStatusStoreTriggerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gitbench-status-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var statePath = Path.Combine(_root, "repos.json");
        _registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
        _sweep = new StartupSweepCoordinator(_gate);
        _store = new RepoStatusStore(
            new IdleOperations(), _registry, new GitService(new RepoActivityTracker()),
            new MessageBus(), _sweep, _gate, _dispatcher);
    }

    [Fact]
    public void Switching_repos_reprobes_the_newly_active_one()
    {
        InitRepo("alpha", "alpha-branch");
        InitRepo("beta", "beta-branch");
        _store.Start();

        SetActive("alpha");
        DrainUntil(() => _store.Active.Value.CurrentBranchName == "alpha-branch", "alpha's probe to land");

        SetActive("beta");
        DrainUntil(() => _store.Active.Value.CurrentBranchName == "beta-branch", "beta's probe to land");
    }

    [Fact]
    public void The_active_repo_is_seeded_without_waiting_for_the_startup_sweep()
    {
        // Opening makes it active, so subscribing at Start must probe it straight away rather than
        // leaving the toolbar Unknown until MarkActiveReady releases the deferred all-repos sweep.
        InitRepo("solo", "solo-branch");

        _store.Start();

        DrainUntil(() => _store.Active.Value.CurrentBranchName == "solo-branch", "the initial probe to land");
    }

    [Fact]
    public void Switching_back_reprobes_rather_than_serving_the_previous_answer()
    {
        InitRepo("alpha", "alpha-branch");
        InitRepo("beta", "beta-branch");
        _store.Start();

        SetActive("alpha");
        DrainUntil(() => _store.Active.Value.CurrentBranchName == "alpha-branch", "alpha's probe to land");
        SetActive("beta");
        DrainUntil(() => _store.Active.Value.CurrentBranchName == "beta-branch", "beta's probe to land");

        // alpha moves while it is not the active repo; switching back must show the new branch.
        Git(Path.Combine(_root, "alpha"), "checkout", "-q", "-b", "alpha-moved");

        SetActive("alpha");
        DrainUntil(() => _store.Active.Value.CurrentBranchName == "alpha-moved", "alpha's re-probe to land");
    }

    // ---- §2 skip / ordering: which triggers still run a summary probe ----
    //
    // On the active repo the file-list reload's `git status` carries the summary, which
    // RepoSnapshotStore ingests — so the status store must NOT probe on those channels, or the
    // working-tree walk runs twice. A counting decorator around the real GitService makes the
    // difference observable.

    [Fact]
    public void WorkingTreeChanged_for_the_active_repo_runs_no_summary_probe()
    {
        using var h = StartActive();
        var before = h.Git.StatusSummaryCalls;

        h.Bus.Broadcast(new WorkingTreeChangedMessage(RepoId("active")));

        // The skip is synchronous (no task is even started), so no drain window can add a call.
        Assert.Equal(before, h.Git.StatusSummaryCalls);
    }

    [Fact]
    public void WorkingTreeChanged_for_a_non_active_repo_runs_one_summary_probe()
    {
        using var h = StartActive();
        var before = h.Git.StatusSummaryCalls;

        h.Bus.Broadcast(new WorkingTreeChangedMessage(RepoId("other")));

        DrainUntil(h.Dispatcher, () => h.Git.StatusSummaryCalls == before + 1, "the non-active probe");
        Quiesce(h);
        Assert.Equal(before + 1, h.Git.StatusSummaryCalls);
    }

    [Fact]
    public void RefsChanged_for_the_active_repo_runs_one_summary_probe()
    {
        using var h = StartActive();
        var before = h.Git.StatusSummaryCalls;

        // A fetch moves ahead/behind without touching the working tree, so the file-list reload
        // never runs — this channel keeps its probe.
        h.Bus.Broadcast(new RefsChangedMessage(RepoId("active")));

        DrainUntil(h.Dispatcher, () => h.Git.StatusSummaryCalls == before + 1, "the refs probe");
        Quiesce(h);
        Assert.Equal(before + 1, h.Git.StatusSummaryCalls);
    }

    [Fact]
    public void RepoRefreshRequested_for_the_active_repo_runs_one_summary_probe()
    {
        using var h = StartActive();
        var before = h.Git.StatusSummaryCalls;

        // The user's explicit retry after a failed load, which ingests nothing — keep the probe.
        h.Bus.Broadcast(new RepoRefreshRequestedMessage(RepoId("active")));

        DrainUntil(h.Dispatcher, () => h.Git.StatusSummaryCalls == before + 1, "the refresh probe");
        Quiesce(h);
        Assert.Equal(before + 1, h.Git.StatusSummaryCalls);
    }

    [Fact]
    public void A_superseded_publish_does_not_write_the_slot()
    {
        var store = NewStore(new GitService(new RepoActivityTracker()), _dispatcher);
        var ingest = (IRepoStatusIngest)store;
        var id = Guid.NewGuid();

        var stale = ingest.Reserve(id);
        ingest.Reserve(id); // a newer observation claims the slot's epoch

        ingest.Publish(id, stale, Summary("superseded"));
        Assert.Null(store.For(id).CurrentBranchName);

        store.Dispose();
    }

    [Fact]
    public void Publish_with_a_null_summary_falls_back_to_a_probe()
    {
        using var h = StartActive();
        var id = RepoId("active");
        var before = h.Git.StatusSummaryCalls;

        // A failed file-list read publishes null; the slot must fall back to the probe it replaced,
        // or root cause D loses its recovery path on this channel.
        var reservation = ((IRepoStatusIngest)h.Store).Reserve(id);
        ((IRepoStatusIngest)h.Store).Publish(id, reservation, null);

        DrainUntil(h.Dispatcher, () => h.Git.StatusSummaryCalls == before + 1, "the fallback probe");
    }

    // A saturated read gate must DEFER a probe, never drop it: the probe parks on the gate while
    // every slot is held, then runs and lands through the same epoch-guarded post once one frees.
    [Fact]
    public void A_saturated_gate_defers_a_probe_until_a_permit_frees()
    {
        using var h = StartActive();
        var other = RepoId("other");

        // Hold every read slot, so the shared gate is saturated by three parked reads.
        var slot1 = h.Gate.Acquire(Guid.NewGuid(), GitReadKind.Commits).GetAwaiter().GetResult();
        var slot2 = h.Gate.Acquire(Guid.NewGuid(), GitReadKind.Commits).GetAwaiter().GetResult();
        var slot3 = h.Gate.Acquire(Guid.NewGuid(), GitReadKind.Commits).GetAwaiter().GetResult();

        var before = h.Git.StatusSummaryCalls;

        // A non-active working-tree change asks for a probe, but no slot is free.
        h.Bus.Broadcast(new WorkingTreeChangedMessage(other));

        // Deferred, not dropped: the probe parks on the gate — its epoch was reserved when it started,
        // but no git status runs while every slot is held.
        Quiesce(h);
        Assert.Equal(before, h.Git.StatusSummaryCalls);

        // Free one slot; the parked probe now runs to completion and lands through the epoch-guarded
        // post, exactly as an unthrottled probe would.
        slot1.Dispose();
        DrainUntil(h.Dispatcher, () => h.Git.StatusSummaryCalls == before + 1,
            "the deferred probe to run once a permit frees");

        slot2.Dispose();
        slot3.Dispose();
    }

    // ---- helpers ----

    private void SetActive(string name) => _registry.SetActive(RepoId(name));

    private Guid RepoId(string name) => _registry.Repos.Single(r => r.DisplayName == name).Id;

    private static GitStatusSummary Summary(string branch) => new(branch, false, false, 0, 0, false);

    private RepoStatusStore NewStore(IGitService git, IUiDispatcher dispatcher)
    {
        var gate = new GitReadGate();
        return new(new IdleOperations(), _registry, git, new MessageBus(), new StartupSweepCoordinator(gate), gate, dispatcher);
    }

    // Two real repos with "active" made the active one, over a store wired to a counting GitService.
    // The store's own startup sweep is never released, so the only probes are the active-repo trigger
    // (§1) plus whatever a test broadcasts — exactly the isolation the existing tests rely on.
    private CountingHarness StartActive()
    {
        InitRepo("active", "active-branch");
        InitRepo("other", "other-branch");
        var h = new CountingHarness(_registry);
        h.Store.Start();
        SetActive("active");
        DrainUntil(h.Dispatcher, () => h.Store.Active.Value.CurrentBranchName == "active-branch", "active's probe to land");
        Quiesce(h);
        return h;
    }

    // Pumps the harness until its probe count stops changing across a short window, so a delta
    // captured next is measured against a quiescent baseline (no straggler probe in flight).
    private static void Quiesce(CountingHarness h)
    {
        int last;
        do
        {
            last = h.Git.StatusSummaryCalls;
            for (var i = 0; i < 10; i++)
            {
                h.Dispatcher.Drain();
                Thread.Sleep(10);
            }
        }
        while (h.Git.StatusSummaryCalls != last);
    }

    private static void DrainUntil(QueuedDispatcher dispatcher, Func<bool> done, string what)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            dispatcher.Drain();
            if (done()) return;
            Thread.Sleep(10);
        }
        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    private void InitRepo(string name, string branch)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        Git(path, "init", "-q", "-b", branch);
        Git(path, "config", "user.name", "Test");
        Git(path, "config", "user.email", "test@example.com");
        Git(path, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(path, "a.txt"), "0");
        Git(path, "add", "a.txt");
        Git(path, "commit", "-qm", "base");
        Assert.Equal(OpenRepoOutcome.Opened, _registry.Open(path));
    }

    // The probe runs off-thread and posts its result back, so pump the dispatcher until the store
    // reflects it. Failing here means no probe was produced at all, which is the regression.
    private void DrainUntil(Func<bool> done, string what) => DrainUntil(_dispatcher, done, what);

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
        _store.Dispose();
        _registry.Dispose();
        try { ForceDelete(new DirectoryInfo(_root)); }
        catch { /* best effort: a leftover temp repo is harmless */ }
    }

    // git marks loose objects read-only, which trips Directory.Delete on Windows; clear attributes
    // depth-first before removing.
    private static void ForceDelete(DirectoryInfo dir)
    {
        if (!dir.Exists) return;
        foreach (var file in dir.GetFiles("*", SearchOption.AllDirectories))
            file.Attributes = FileAttributes.Normal;
        dir.Delete(recursive: true);
    }

    // Collects posted work so the test thread decides when results land, mirroring how the real
    // app drains the UI queue once per frame.
    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly ConcurrentQueue<Action> _queue = new();

        public void Post(Action action) => _queue.Enqueue(action);

        public void Drain()
        {
            while (_queue.TryDequeue(out var action)) action();
        }
    }

    // A started RepoStatusStore wired to a counting GitService plus the bus it subscribes to, so a
    // test can broadcast on that bus and read StatusSummaryCalls. Its own dispatcher and startup
    // sweep isolate it from the fixture's shared store.
    private sealed class CountingHarness : IDisposable
    {
        public readonly MessageBus Bus = new();
        public readonly QueuedDispatcher Dispatcher = new();
        public readonly CountingGitService Git = new(new GitService(new RepoActivityTracker()));
        public readonly GitReadGate Gate = new();
        public readonly RepoStatusStore Store;

        public CountingHarness(IRepoRegistry registry) =>
            Store = new RepoStatusStore(new IdleOperations(), registry, Git, Bus, new StartupSweepCoordinator(Gate), Gate, Dispatcher);

        public void Dispose() => Store.Dispose();
    }

    private sealed class IdleOperations : IRepoOperationsStore
    {
        private readonly State<RepoOperations> _active = new(RepoOperations.Idle);

        public IReadable<RepoOperations> Active => _active;
        public bool HasUnseenError(Guid repoId) => false;
        public bool IsBusy(Guid repoId) => false;
        public void Push(Repo repo, bool force = false) { }
        public void Pull(Repo repo, PullStrategy? strategy = null) { }
        public void Fetch(Repo repo) { }
    }
}
