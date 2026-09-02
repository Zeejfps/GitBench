using System.Collections.Concurrent;
using System.Diagnostics;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Messages;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

// §6 acceptance, expressed against the real stores: the active repo's own switch fan-out (commits +
// branches + local) must never be throttled by the shared read gate — the gate is sized to exactly
// that fan-out — and a user mutation must never queue behind a background read, because the gate and
// GitRepoLocks are disjoint by construction.
public sealed class GitReadGateStoreTests : IDisposable
{
    private readonly string _root;
    private readonly RepoRegistry _registry;
    private readonly QueuedDispatcher _dispatcher = new();

    public GitReadGateStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gitbench-readgate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var statePath = Path.Combine(_root, "repos.json");
        _registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
    }

    [Fact]
    public void A_switch_fans_out_three_reads_that_all_acquire_concurrently()
    {
        var id = InitRepo("solo", "solo-branch");
        _registry.SetActive(id);

        var git = new GitService(new RepoActivityTracker());
        var probe = new ConcurrencyProbeGate(new GitReadGate(), GitReadGate.MaxConcurrentReads);
        using var store = new RepoSnapshotStore(
            _registry, git, git, git, git, new MessageBus(), new NoIngest(), probe, _dispatcher);

        // Subscribing fires OnActiveChanged for the active repo, which issues exactly the three-slice
        // fan-out. All three must reach the gate at once — if the gate were sized to 2, the third
        // would block on the gate's WaitAsync and the peak would never reach three.
        store.Start();

        Assert.True(probe.WaitForPeak(TimeSpan.FromSeconds(10)),
            "the active repo's three-slice fan-out must all acquire concurrently, never self-throttled");
        Assert.Equal(GitReadGate.MaxConcurrentReads, probe.Peak);

        DrainFor(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void A_saturated_gate_never_delays_a_mutation()
    {
        var id = InitRepo("repo", "main");
        var repo = _registry.Repos.Single(r => r.Id == id);
        var git = new GitService(new RepoActivityTracker());

        // Hold every read slot: the shared read gate is fully saturated.
        var gate = new GitReadGate();
        var held = new List<IGitReadGate.Permit>();
        for (var i = 0; i < GitReadGate.MaxConcurrentReads; i++)
            held.Add(gate.Acquire(Guid.NewGuid(), GitReadKind.Status).GetAwaiter().GetResult());

        // A user stage takes GitRepoLocks, never the read gate, so it completes promptly regardless.
        File.WriteAllText(Path.Combine(_root, "repo", "new.txt"), "x");
        var sw = Stopwatch.StartNew();
        var outcome = git.Stage(repo, new[] { "new.txt" });
        sw.Stop();

        Assert.True(outcome is GitOutcome.Success, outcome.FailureMessage);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"a mutation waited on the read gate ({sw.Elapsed})");

        foreach (var permit in held) permit.Dispose();
    }

    // ---- helpers ----

    // Wraps a real gate and, after each read acquires a real permit, holds it at a barrier until
    // `target` reads have arrived — so max-in-flight is observable and the reads provably overlap.
    private sealed class ConcurrencyProbeGate : IGitReadGate
    {
        private readonly IGitReadGate _inner;
        private readonly int _target;
        private readonly ManualResetEventSlim _peakReached = new(false);
        private int _arrived;
        private int _inFlight;
        private int _peak;

        public ConcurrencyProbeGate(IGitReadGate inner, int target)
        {
            _inner = inner;
            _target = target;
        }

        public int Peak => Volatile.Read(ref _peak);
        public bool WaitForPeak(TimeSpan timeout) => _peakReached.Wait(timeout);

        public async Task<IGitReadGate.Permit> Acquire(Guid repoId, GitReadKind kind)
        {
            var permit = await _inner.Acquire(repoId, kind);
            var now = Interlocked.Increment(ref _inFlight);
            UpdatePeak(now);
            if (Interlocked.Increment(ref _arrived) >= _target) _peakReached.Set();
            _peakReached.Wait(TimeSpan.FromSeconds(5));
            return new IGitReadGate.Permit(() =>
            {
                Interlocked.Decrement(ref _inFlight);
                permit.Dispose();
            });
        }

        public void SetForegroundRepo(Guid? repoId) => _inner.SetForegroundRepo(repoId);
        public TimeSpan? LastStatusReadDuration(Guid repoId) => _inner.LastStatusReadDuration(repoId);
        public bool HasOutstandingReads(Guid repoId) => _inner.HasOutstandingReads(repoId);

        private void UpdatePeak(int now)
        {
            int seen;
            do { seen = Volatile.Read(ref _peak); }
            while (now > seen && Interlocked.CompareExchange(ref _peak, now, seen) != seen);
        }
    }

    private sealed class NoIngest : IRepoStatusIngest
    {
        public int Reserve(Guid repoId) => 0;
        public void Publish(Guid repoId, int reservation, GitStatusSummary? summary) { }
    }

    private void DrainFor(TimeSpan window)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < window)
        {
            _dispatcher.Drain();
            Thread.Sleep(10);
        }
    }

    private Guid InitRepo(string name, string branch)
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
        _registry.Dispose();
        DirectoryTree.Delete(_root);
    }

    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly ConcurrentQueue<Action> _queue = new();
        public void Post(Action action) => _queue.Enqueue(action);
        public void Drain()
        {
            while (_queue.TryDequeue(out var action)) action();
        }
    }
}
