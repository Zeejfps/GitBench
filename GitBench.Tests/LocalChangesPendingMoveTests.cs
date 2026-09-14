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
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

// A whole-file stage/unstage never rearranges the file lists ahead of git. The rows are marked in
// flight (drawn dimmed, spinner in the header) while the git op runs, and only the post-mutation
// reload moves them — so a 16k-file Stage All costs the UI the same as a one-file stage, instead of
// the minutes the old per-path list copy-and-sort took on the UI thread.
public sealed class LocalChangesPendingMoveTests : IDisposable
{
    private sealed class NoopShell : IPlatformShell
    {
        public void OpenFolder(string path) { }
        public void OpenTerminal(string path) { }
        public void OpenFile(string path) { }
        public void OpenUrl(string url) { }
    }

    private sealed class NoopClipboard : IClipboard
    {
        public void SetText(string text) { }
        public string? GetText() => null;
    }

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
    private readonly LocalChangesViewModel _vm;
    private readonly Repo _repo;
    private int _workingTreeChanges;

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

        _preferences = new PreferencesService(Preferences.Default, Path.Combine(_base, "prefs.json"));
        _vm = new LocalChangesViewModel(
            _registry, _git, _git, _git, _git, _git, _dispatcher, new FrameTicker(), _bus,
            new LocalChangesSelectionStore(), new NoopShell(), new NoopClipboard(),
            _preferences, _store, _loc, new NoUnsavedEdits());
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
        _preferences.Dispose();
        _registry.Dispose();
        _loc.Dispose();
        DirectoryTree.Delete(_base);
    }
}
