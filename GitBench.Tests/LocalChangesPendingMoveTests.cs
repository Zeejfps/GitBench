using System.Diagnostics;
using GitBench.App;
using GitBench.Features.Branches;
using GitBench.Features.Commits;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Features.Submodules;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Platform;
using ZGF.Gui;
using ZGF.Gui.Desktop.Input;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

// A whole-file stage/unstage never rearranges the file lists ahead of git. The rows are marked in
// flight (drawn dimmed, spinner in the header) while the git op runs, and only the post-mutation
// reload moves them — so a 16k-file Stage All costs the UI the same as a one-file stage, instead of
// the minutes the old per-path list copy-and-sort took on the UI thread. A large request runs as
// chunks, each reconciled by its own reload, so the rows land in waves the user can watch.
public sealed class LocalChangesPendingMoveTests : IDisposable
{
    private sealed class FakeSnapshotStore : IRepoSnapshotStore
    {
        public State<Fetched<LocalChangesData>?> LocalState { get; } = new(null);
        public IReadable<Fetched<CommitSnapshot>?> Commits { get; } = new State<Fetched<CommitSnapshot>?>(null);
        public IReadable<Fetched<BranchListing>?> Branches { get; } = new State<Fetched<BranchListing>?>(null);
        public IReadable<Fetched<LocalChangesData>?> LocalChanges => LocalState;
    }

    private readonly string _base;
    private readonly string _root;
    private readonly RepoRegistry _registry;
    private readonly CountingGitService _git;
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly MessageBus _bus = new();
    private readonly PreferencesService _preferences;
    private readonly LocalizationService _loc = new(new State<Locale>(Locale.En));
    private readonly FakeSnapshotStore _store = new();
    private readonly RepoIndexOperationsStore _indexOps;
    private readonly LocalChangesViewModel _vm;
    private readonly Repo _repo;
    private int _workingTreeChanges;
    private int _errorDialogs;

    public LocalChangesPendingMoveTests()
    {
        // App state files live beside the repo, not inside it, so they never show up as untracked.
        _base = Path.Combine(Path.GetTempPath(), "gitbench-pending-move-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_base, "repo");
        Directory.CreateDirectory(_root);
        Git("init", "--initial-branch=main");
        Git("config", "user.email", "test@test");
        Git("config", "user.name", "test");
        WriteFile("a.txt", "one\n");
        Git("add", "a.txt");
        Git("commit", "-m", "base");
        // Two unstaged entries: a modified tracked file and a new one, so staging one of them
        // leaves the other behind for the bulk-action gate to re-enable against.
        WriteFile("a.txt", "two\n");
        WriteFile("b.txt", "new\n");

        var statePath = Path.Combine(_base, "repos.json");
        _registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
        Assert.Equal(OpenRepoOutcome.Opened, _registry.Open(_root));
        _registry.SetActive(_registry.Repos.Single().Id);
        _repo = _registry.Active.Value!;

        _git = new CountingGitService(new GitService(new RepoActivityTracker()));
        _bus.Subscribe<WorkingTreeChangedMessage>(_ => Interlocked.Increment(ref _workingTreeChanges));
        _bus.Subscribe<ShowOperationErrorMessage>(_ => _errorDialogs++);

        _preferences = new PreferencesService(Preferences.Default, Path.Combine(_base, "prefs.json"));
        _indexOps = StartedIndexOperationsStore.Create(_registry, _bus, _loc, _dispatcher);
        _vm = new LocalChangesViewModel(
            _registry, _git, _git, _git, _git, _git, _dispatcher, new FrameTicker(), _bus,
            _indexOps, new LocalChangesSelectionStore(), new FakeShell(), new FakeClipboard(),
            _preferences, _store, new NoStatusIngest(), _loc, new NoUnsavedEdits());
        PushSnapshot();
    }

    [Fact]
    public void Stage_marks_the_rows_in_flight_and_the_reload_moves_them()
    {
        using var gate = new ManualResetEventSlim(false);
        _git.StageGate = gate;

        _vm.Stage(new[] { "a.txt" });

        // In flight: the row is still on the unstaged side, only marked pending; bulk actions wait.
        Assert.Equal(new[] { "a.txt" }, _vm.PendingPaths.Value.OrderBy(p => p));
        Assert.Contains("a.txt", Unstaged());
        Assert.DoesNotContain("a.txt", Staged());
        Assert.False(_vm.StageAll.CanExecute.Value);

        gate.Set();
        DrainUntil(() => _workingTreeChanges == 1, "the stage to land");

        // The git op finished, but the rows only move when the store's reload reconciles them.
        Assert.Equal(new[] { "a.txt" }, _vm.PendingPaths.Value.OrderBy(p => p));
        Assert.Contains("a.txt", Unstaged());

        PushSnapshot();

        Assert.Empty(_vm.PendingPaths.Value);
        Assert.Equal(new[] { "b.txt" }, Unstaged());
        Assert.Equal(new[] { "a.txt" }, Staged());
        Assert.Equal(new[] { "a.txt" }, _vm.Selection.Value.PathsOn(DiffSide.Staged));
        Assert.True(_vm.StageAll.CanExecute.Value);
    }

    [Fact]
    public void A_failed_stage_clears_the_in_flight_mark_and_leaves_the_rows_put()
    {
        _git.StageFailure = "index.lock is held";

        _vm.Stage(new[] { "a.txt" });
        Assert.Equal(new[] { "a.txt" }, _vm.PendingPaths.Value.OrderBy(p => p));

        DrainUntil(() => _workingTreeChanges == 1, "the failure to land");

        Assert.Empty(_vm.PendingPaths.Value);
        Assert.Equal(new[] { "a.txt", "b.txt" }, Unstaged());
        Assert.Empty(Staged());
        Assert.True(_vm.StageAll.CanExecute.Value);
    }

    [Fact]
    public void A_path_already_in_flight_is_not_staged_again()
    {
        using var gate = new ManualResetEventSlim(false);
        _git.StageGate = gate;

        _vm.Stage(new[] { "a.txt" });
        _vm.Stage(new[] { "a.txt" });
        gate.Set();
        DrainUntil(() => _workingTreeChanges == 1, "the stage to land");

        Assert.Equal(1, _git.StageCalls);
        PushSnapshot();
        Assert.Equal(new[] { "a.txt" }, Staged());
    }

    [Fact]
    public void A_move_keeps_its_rows_in_flight_across_a_repo_switch_and_lands_on_return()
    {
        using var gate = new ManualResetEventSlim(false);
        _git.StageGate = gate;
        var preOp = _store.LocalState.Value;

        _vm.Stage(new[] { "a.txt" });
        Assert.Equal(new[] { "a.txt" }, _vm.PendingPaths.Value.OrderBy(p => p));

        // Away: the other repo shows nothing in flight. Back: the move is still marked, even though
        // the snapshot store hands back the cached pre-op lists first.
        var other = OpenOtherRepo();
        _registry.SetActive(other.Id);
        _store.LocalState.Value = null;
        Assert.Empty(_vm.PendingPaths.Value);

        _registry.SetActive(_repo.Id);
        _store.LocalState.Value = preOp;
        Assert.Equal(new[] { "a.txt" }, _vm.PendingPaths.Value.OrderBy(p => p));
        Assert.Contains("a.txt", Unstaged());

        gate.Set();
        DrainUntil(() => _workingTreeChanges == 1, "the stage to land");
        PushSnapshot();

        Assert.Empty(_vm.PendingPaths.Value);
        Assert.Equal(new[] { "a.txt" }, Staged());
        Assert.Equal(new[] { "a.txt" }, _vm.Selection.Value.PathsOn(DiffSide.Staged));
    }

    [Fact]
    public void A_finished_move_settles_only_on_a_snapshot_that_reflects_it()
    {
        var preOp = _store.LocalState.Value;

        _vm.Stage(new[] { "a.txt" });
        DrainUntil(() => _workingTreeChanges == 1, "the stage to land");

        // A snapshot read before the op (the cache handed back on switch-back) still shows the file
        // unstaged: the rows stay in flight rather than flashing back un-marked.
        _store.LocalState.Value = null;
        _store.LocalState.Value = preOp;
        Assert.Equal(new[] { "a.txt" }, _vm.PendingPaths.Value.OrderBy(p => p));
        Assert.Contains("a.txt", Unstaged());

        PushSnapshot();
        Assert.Empty(_vm.PendingPaths.Value);
        Assert.Equal(new[] { "a.txt" }, Staged());
    }

    [Fact]
    public void A_large_stage_lands_in_batches()
    {
        WriteExtraFiles(60);
        PushSnapshot();
        var all = UnstagedInListOrder();
        Assert.Equal(62, all.Count);

        // One permit per chunk, so every chunk's landing can be observed before the next runs.
        using var permits = new SemaphoreSlim(1);
        _git.StagePermits = permits;

        _vm.Stage(all);
        Assert.Equal(all.OrderBy(p => p), _vm.PendingPaths.Value.OrderBy(p => p));

        var chunks = 0;
        var landed = 0;
        while (landed < all.Count)
        {
            var before = _workingTreeChanges;
            DrainUntil(() => _workingTreeChanges > before, "a chunk to land");
            chunks++;
            Assert.Equal(chunks, _git.StageCalls);
            PushSnapshot();

            // Chunks land in request order: the staged side is a prefix of the request, the pending
            // set is the rest, and the selection follows everything landed so far.
            var staged = Staged();
            Assert.True(staged.Count > landed, "each chunk lands more of the request");
            if (chunks == 1) Assert.Equal(RepoIndexOperationsStore.InitialChunkSize, staged.Count);
            landed = staged.Count;
            Assert.Equal(all.Take(landed).OrderBy(p => p), staged);
            Assert.Equal(all.Skip(landed).OrderBy(p => p), _vm.PendingPaths.Value.OrderBy(p => p));
            Assert.Equal(all.Take(landed).OrderBy(p => p), _vm.Selection.Value.PathsOn(DiffSide.Staged).OrderBy(p => p));

            permits.Release();
        }

        Assert.True(chunks >= 2, "a 62-file request runs as more than one chunk");
        Assert.Empty(_vm.PendingPaths.Value);
        Assert.True(_vm.UnstageAll.CanExecute.Value);
    }

    [Fact]
    public void A_click_elsewhere_during_a_batched_stage_keeps_the_users_selection()
    {
        WriteExtraFiles(60);
        PushSnapshot();
        var all = UnstagedInListOrder();
        // b.txt stays out of the request, to click on while the rest lands.
        var request = all.Where(p => p != "b.txt").ToList();
        using var permits = new SemaphoreSlim(1);
        _git.StagePermits = permits;

        _vm.Stage(request);
        var before = _workingTreeChanges;
        DrainUntil(() => _workingTreeChanges > before, "the first chunk to land");
        _vm.SelectRow(new FileRowRef(DiffSide.Unstaged, "b.txt", IsFolder: false), InputModifiers.None);
        PushSnapshot();

        // The first wave landed without moving the selection off the user's own click.
        Assert.Equal(RepoIndexOperationsStore.InitialChunkSize, Staged().Count);
        Assert.Equal(new[] { "b.txt" }, _vm.Selection.Value.PathsOn(DiffSide.Unstaged));
        Assert.Empty(_vm.Selection.Value.PathsOn(DiffSide.Staged));

        while (_vm.PendingPaths.Value.Count > 0)
        {
            permits.Release();
            before = _workingTreeChanges;
            DrainUntil(() => _workingTreeChanges > before, "a chunk to land");
            PushSnapshot();
        }

        Assert.Equal(request.OrderBy(p => p), Staged());
        Assert.Equal(new[] { "b.txt" }, _vm.Selection.Value.PathsOn(DiffSide.Unstaged));
        Assert.Empty(_vm.Selection.Value.PathsOn(DiffSide.Staged));
    }

    [Fact]
    public void Intermediate_chunks_do_not_broadcast_while_a_reload_is_outstanding()
    {
        WriteExtraFiles(80);
        PushSnapshot();
        var all = UnstagedInListOrder();
        // Slower than the target keeps every chunk at the initial size: 25 + 25 + 25 + 7.
        _git.StageDelay = RepoIndexOperationsStore.TargetChunkDuration + TimeSpan.FromMilliseconds(50);

        _vm.Stage(all);
        DrainUntil(() => _git.StageCalls == 4 && _workingTreeChanges >= 2, "the request to finish");

        // The first chunk asks for a reload; the ones after it see that reload still outstanding
        // (no snapshot came back) and stay quiet; the last always asks.
        Assert.Equal(2, _workingTreeChanges);

        PushSnapshot();
        Assert.Empty(_vm.PendingPaths.Value);
        Assert.Equal(all.OrderBy(p => p), Staged());
    }

    [Fact]
    public void A_failed_chunk_drops_the_rest_of_the_request()
    {
        WriteExtraFiles(60);
        PushSnapshot();
        var all = UnstagedInListOrder();
        var first = all.Take(RepoIndexOperationsStore.InitialChunkSize).ToList();
        _git.StageFailure = "index.lock is held";
        _git.StageFailureAfterCalls = 1;

        _vm.Stage(all);
        // The first chunk's reload, then the failure's.
        DrainUntil(() => _workingTreeChanges == 2, "the failure to land");

        Assert.Equal(2, _git.StageCalls);
        Assert.Equal(1, _errorDialogs);
        Assert.Empty(_vm.PendingPaths.Value);

        PushSnapshot();
        Assert.Equal(first.OrderBy(p => p), Staged());
        Assert.Equal(2, _git.StageCalls);
        Assert.True(_vm.StageAll.CanExecute.Value);
    }

    [Fact]
    public void A_failure_while_the_repo_is_not_active_waits_as_a_badge()
    {
        using var gate = new ManualResetEventSlim(false);
        _git.StageGate = gate;
        _git.StageFailure = "index.lock is held";
        var preOp = _store.LocalState.Value;

        _vm.Stage(new[] { "a.txt" });

        var other = OpenOtherRepo();
        _registry.SetActive(other.Id);
        _store.LocalState.Value = null;

        gate.Set();
        DrainUntil(() => _workingTreeChanges == 1, "the failure to land");

        Assert.Equal(0, _errorDialogs);
        Assert.True(_indexOps.HasUnseenError(_repo.Id));
        Assert.False(_indexOps.HasUnseenError(other.Id));

        _registry.SetActive(_repo.Id);
        _store.LocalState.Value = preOp;

        Assert.Equal(1, _errorDialogs);
        Assert.False(_indexOps.HasUnseenError(_repo.Id));
        Assert.Empty(_vm.PendingPaths.Value);
        Assert.Equal(new[] { "a.txt", "b.txt" }, Unstaged());
    }

    private IReadOnlyList<string> UnstagedInListOrder() => _vm.Unstaged.Value.Select(f => f.Path).ToList();

    private void WriteExtraFiles(int count)
    {
        for (var i = 0; i < count; i++)
            WriteFile($"f{i:000}.txt", $"{i}\n");
    }

    private Repo OpenOtherRepo()
    {
        var path = Path.Combine(_base, "other");
        Directory.CreateDirectory(path);
        var psi = new ProcessStartInfo("git", "init --initial-branch=main")
        {
            WorkingDirectory = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using (var p = Process.Start(psi)!) p.WaitForExit();
        Assert.Equal(OpenRepoOutcome.Opened, _registry.Open(path));
        return _registry.Repos.Single(r => r.Path == path);
    }

    private IReadOnlyList<string> Unstaged() => _vm.Unstaged.Value.Select(f => f.Path).OrderBy(p => p).ToList();
    private IReadOnlyList<string> Staged() => _vm.Staged.Value.Select(f => f.Path).OrderBy(p => p).ToList();

    // Stands in for the snapshot store's reload: reads the real working tree and pushes it.
    private void PushSnapshot()
    {
        var snap = ((Fetched<LocalChangesSnapshot>.Ok)_git.GetLocalChanges(_repo)).Value;
        _store.LocalState.Value = new Fetched<LocalChangesData>.Ok(
            new LocalChangesData(snap, Array.Empty<SubmoduleInfo>()));
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

    private void WriteFile(string name, string content)
        => File.WriteAllText(Path.Combine(_root, name), content);

    private void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("commit.gpgsign=false");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
    }

    public void Dispose()
    {
        _vm.Dispose();
        _indexOps.Dispose();
        _preferences.Dispose();
        _registry.Dispose();
        _loc.Dispose();
        DirectoryTree.Delete(_base);
    }
}
