using System.Diagnostics;
using GitBench.App;
using GitBench.Features.Branches;
using GitBench.Features.Commits;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Features.Submodules;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Platform;
using ZGF.Gui;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

// The commit box belongs to the repository, not to the window: what is typed in one repo is parked
// under it on the way out and put back on the way in, and neither repo can see the other's message.
public sealed class LocalChangesCommitDraftTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-commit-draft-");
    private readonly GitService _git = new(new NullActivityTracker());
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly LocalizationService _loc = new(new State<Locale>(Locale.En));
    private readonly FakeSnapshotStore _store = new();
    private readonly RepoRegistry _registry;
    private readonly LocalChangesViewModel _vm;
    private readonly string _pathA;
    private readonly string _pathB;
    private readonly Guid _repoA;
    private readonly Guid _repoB;

    public LocalChangesCommitDraftTests()
    {
        _pathA = SeedRepo("a", "Seed A");
        _pathB = SeedRepo("b", "Seed B");

        var statePath = Path.Combine(_dir.Path, "repos.json");
        _registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
        Assert.Equal(OpenRepoOutcome.Opened, _registry.Open(_pathA));
        Assert.Equal(OpenRepoOutcome.Opened, _registry.Open(_pathB));
        _repoA = _registry.Repos.Single(r => r.Path == _pathA).Id;
        _repoB = _registry.Repos.Single(r => r.Path == _pathB).Id;
        _registry.SetActive(_repoA);

        _vm = new LocalChangesViewModel(
            _registry, _git, _git, _git, _git, _git, _dispatcher, new FrameTicker(), new MessageBus(),
            new LocalChangesSelectionStore(), new NoopShell(), new NoopClipboard(),
            new PreferencesService(Preferences.Default, Path.Combine(_dir.Path, "prefs.json")),
            _store, _loc);

        Push();
    }

    [Fact]
    public void SwitchingAwayClearsTheBoxAndComingBackRestoresIt()
    {
        _vm.SetTitle("Fix the parser");
        _vm.SetDescription("It choked on tabs.");

        Activate(_repoB);
        Assert.Equal(string.Empty, _vm.Title.Value);
        Assert.Equal(string.Empty, _vm.Description.Value);

        Activate(_repoA);
        Assert.Equal("Fix the parser", _vm.Title.Value);
        Assert.Equal("It choked on tabs.", _vm.Description.Value);
    }

    [Fact]
    public void EachRepoKeepsItsOwnMessage()
    {
        _vm.SetTitle("Written in A");
        Activate(_repoB);
        _vm.SetTitle("Written in B");

        Activate(_repoA);
        Assert.Equal("Written in A", _vm.Title.Value);

        Activate(_repoB);
        Assert.Equal("Written in B", _vm.Title.Value);
    }

    // The box swaps on the registry, not on the repo's status landing — otherwise the outgoing
    // repo's message sits in front of the incoming repo for as long as `git status` takes.
    [Fact]
    public void TheBoxSwapsBeforeTheNewRepoSnapshotArrives()
    {
        _vm.SetTitle("Written in A");

        _registry.SetActive(_repoB);

        Assert.Equal(string.Empty, _vm.Title.Value);
    }

    // An amend shows HEAD's message; that is the commit being rewritten, not something the person
    // typed, so a switch parks the draft the amend displaced.
    [Fact]
    public void AnAmendedMessageIsNotParkedAsTheDraft()
    {
        _vm.SetTitle("Half-written");
        _vm.SetAmend(true);
        Assert.Equal("Seed A", _vm.Title.Value);

        Activate(_repoB);
        Activate(_repoA);

        Assert.False(_vm.Amend.Value);
        Assert.Equal("Half-written", _vm.Title.Value);
    }

    [Fact]
    public void CommittingConsumesTheDraft()
    {
        File.WriteAllText(Path.Combine(_pathA, "a.txt"), "changed\n");
        Git(_pathA, "add", "a.txt");
        Push();

        _vm.SetTitle("Real commit");
        _vm.Commit();
        DrainUntil(() => !_vm.CommitBusy.Value, "the commit to finish");

        Activate(_repoB);
        Activate(_repoA);

        Assert.Equal(string.Empty, _vm.Title.Value);
    }

    // A merge that starts while you are looking at the repo still pre-fills the box with git's
    // merge message, whatever was in it.
    [Fact]
    public void AMergeStartingUnderYouStillSeedsTheBox()
    {
        _vm.SetTitle("Half-written");

        Push("Merge branch 'topic'");

        Assert.True(_vm.IsMerging.Value);
        Assert.Equal("Merge branch 'topic'", _vm.Title.Value);
    }

    // Returning to a repo that was already merging is not the merge starting: the box has just been
    // refilled from that repo's draft, which is the merge message as it was left.
    [Fact]
    public void ReturningToAMergingRepoKeepsTheEditedMergeMessage()
    {
        Push("Merge branch 'topic'");
        _vm.SetTitle("Merge branch 'topic' into main");

        Activate(_repoB);
        Activate(_repoA, "Merge branch 'topic'");

        Assert.True(_vm.IsMerging.Value);
        Assert.Equal("Merge branch 'topic' into main", _vm.Title.Value);
    }

    private sealed class FakeSnapshotStore : IRepoSnapshotStore
    {
        public State<Fetched<LocalChangesData>?> LocalState { get; } = new(null);
        public IReadable<Fetched<CommitSnapshot>?> Commits { get; } = new State<Fetched<CommitSnapshot>?>(null);
        public IReadable<Fetched<BranchListing>?> Branches { get; } = new State<Fetched<BranchListing>?>(null);
        public IReadable<Fetched<LocalChangesData>?> LocalChanges => LocalState;
    }

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

    private void Activate(Guid repoId, string? mergeMessage = null)
    {
        _registry.SetActive(repoId);
        Push(mergeMessage);
    }

    // Stands in for the snapshot store pushing the active repo's working-tree data.
    private void Push(string? mergeMessage = null)
    {
        var repo = _registry.Active.Value!;
        var snap = ((Fetched<LocalChangesSnapshot>.Ok)_git.GetLocalChanges(repo)).Value;
        _store.LocalState.Value = new Fetched<LocalChangesData>.Ok(
            new LocalChangesData(snap, Array.Empty<SubmoduleInfo>(), mergeMessage));
    }

    private string SeedRepo(string name, string message)
    {
        var path = Path.Combine(_dir.Path, name);
        Directory.CreateDirectory(path);
        Git(path, "init", "--initial-branch=main");
        Git(path, "config", "user.email", "test@test");
        Git(path, "config", "user.name", "test");
        File.WriteAllText(Path.Combine(path, "a.txt"), "one\n");
        Git(path, "add", "a.txt");
        Git(path, "-c", "commit.gpgsign=false", "commit", "-m", message);
        return path;
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

    private static void Git(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
    }

    public void Dispose()
    {
        _vm.Dispose();
        _loc.Dispose();
        _dir.Dispose();
    }
}
