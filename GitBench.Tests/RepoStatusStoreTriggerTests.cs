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
    private readonly StartupSweepCoordinator _sweep = new();
    private readonly RepoStatusStore _store;

    public RepoStatusStoreTriggerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gitbench-status-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var statePath = Path.Combine(_root, "repos.json");
        _registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
        _store = new RepoStatusStore(
            new IdleOperations(), _registry, new GitService(new RepoActivityTracker()),
            new MessageBus(), _sweep, _dispatcher);
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

    // ---- helpers ----

    private void SetActive(string name) => _registry.SetActive(_registry.Repos.Single(r => r.DisplayName == name).Id);

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
    private void DrainUntil(Func<bool> done, string what)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            _dispatcher.Drain();
            if (done()) return;
            Thread.Sleep(10);
        }
        throw new TimeoutException($"Timed out waiting for {what}.");
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
