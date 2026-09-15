using System.Diagnostics;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Messages;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

// §2's watch item as an assertion: RepoStatusStore skips the active repo's working-tree probe, so the
// only path from a working-tree change to its ahead/behind/dirty slot is RepoSnapshotStore's file-list
// read publishing the summary it parsed in the same pass. This drives BOTH real stores over one real
// MessageBus and a drain-on-demand dispatcher; it fails if either store's active-repo condition drifts
// from the other's (the skip and the reload would then disagree and the toolbar would freeze).
public sealed class StatusIngestIntegrationTests : IDisposable
{
    private readonly string _root;
    private readonly RepoRegistry _registry;
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly MessageBus _bus = new();
    private readonly GitReadGate _gate = new();
    private readonly StartupSweepCoordinator _sweep;
    private readonly RepoStatusStore _status;
    private readonly RepoSnapshotStore _snapshots;

    public StatusIngestIntegrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gitbench-ingest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var statePath = Path.Combine(_root, "repos.json");
        _registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
        var git = new GitService(new RepoActivityTracker());
        _sweep = new StartupSweepCoordinator(_gate);
        var head = new SettledHead();
        _status = new RepoStatusStore(new IdleRemoteOperations(), new IdleIndexOperations(), _registry, git, _bus, _gate, _dispatcher, head, head);
        _snapshots = new RepoSnapshotStore(_registry, git, git, git, git, _bus, _status, _gate, _dispatcher);
    }

    [Fact]
    public void An_active_repo_working_tree_change_reaches_the_status_store_via_ingest()
    {
        InitRepo("solo", "solo-branch");
        _status.Start();
        _snapshots.Start();
        SetActive("solo");

        DrainUntil(
            () => _status.Active.Value.CurrentBranchName == "solo-branch" && !_status.Active.Value.IsDirty,
            "the clean status to land");

        // A real working-tree change plus the watcher's message the snapshot store reloads on.
        File.WriteAllText(Path.Combine(_root, "solo", "dirty.txt"), "x");
        _bus.Broadcast(new WorkingTreeChangedMessage(RepoId("solo")));

        DrainUntil(() => _status.Active.Value.IsDirty, "the ingested dirty flag to land");
    }

    // ---- helpers ----

    private void SetActive(string name) => _registry.SetActive(RepoId(name));

    private Guid RepoId(string name) => _registry.Repos.Single(r => r.DisplayName == name).Id;

    private void InitRepo(string name, string branch)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        TestGit.Init(path, branch);
        File.WriteAllText(Path.Combine(path, "a.txt"), "0");
        Git(path, "add", "a.txt");
        Git(path, "commit", "-qm", "base");
        Assert.Equal(OpenRepoOutcome.Opened, _registry.Open(path));
    }

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

    private static string Git(string cwd, params string[] args) => TestGit.Run(cwd, args);

    public void Dispose()
    {
        _snapshots.Dispose();
        _status.Dispose();
        _registry.Dispose();
        DirectoryTree.Delete(_root);
    }
}
