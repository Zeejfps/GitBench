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

// §10: ticking Amend must not run git status or the index-vs-HEAD^ diff on the UI thread. Entry
// reads only the cheap single-object head message synchronously and seeds the staged panel with the
// index-vs-HEAD list the panel already holds; the amend diff is deferred to an async refresh.
public sealed class LocalChangesAmendEntryTests : IDisposable
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

    private readonly string _root;
    private readonly RepoRegistry _registry;
    private readonly CountingGitService _git;
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly PreferencesService _preferences;
    private readonly LocalizationService _loc = new(new State<Locale>(Locale.En));
    private readonly FakeSnapshotStore _store = new();
    private readonly LocalChangesViewModel _vm;
    private readonly Guid _repoId;

    public LocalChangesAmendEntryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gitbench-amend-entry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Git("init", "--initial-branch=main");
        Git("config", "user.email", "test@test");
        Git("config", "user.name", "test");
        WriteFile("a.txt", "one\n");
        Git("add", "a.txt");
        Git("commit", "-m", "base");
        WriteFile("a.txt", "two\n");
        Git("add", "a.txt");
        Git("commit", "-m", "second");
        // Stage a brand-new file, so the index-vs-HEAD seed is [b.txt] while the index-vs-HEAD^ amend
        // diff is [a.txt, b.txt] — a clear, distinct replacement.
        WriteFile("b.txt", "new\n");
        Git("add", "b.txt");

        var statePath = Path.Combine(_root, "repos.json");
        _registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
        Assert.Equal(OpenRepoOutcome.Opened, _registry.Open(_root));
        _repoId = _registry.Repos.Single().Id;
        _registry.SetActive(_repoId);

        _git = new CountingGitService(new GitService(new RepoActivityTracker()));

        // Seed the store's local-changes slice the way the panel would show it, so the VM's
        // _stagedFromIndex is populated before amend entry.
        var repo = _registry.Active.Value!;
        var snap = ((Fetched<LocalChangesSnapshot>.Ok)_git.GetLocalChanges(repo)).Value;
        _store.LocalState.Value = new Fetched<LocalChangesData>.Ok(
            new LocalChangesData(snap, Array.Empty<SubmoduleInfo>()));

        _preferences = new PreferencesService(Preferences.Default, Path.Combine(_root, "prefs.json"));
        _vm = new LocalChangesViewModel(
            _registry, _git, _dispatcher, new FrameTicker(), new MessageBus(),
            new LocalChangesSelectionStore(), new NoopShell(), new NoopClipboard(),
            _preferences, _store, _loc);
    }

    [Fact]
    public void SetAmend_seeds_staged_synchronously_and_defers_the_diff()
    {
        // The store push at construction seeded the index-vs-HEAD staged list.
        Assert.Equal(new[] { "b.txt" }, _vm.Staged.Value.Select(f => f.Path).ToArray());

        // Park the deferred diff so it cannot run until we release it.
        using var gate = new System.Threading.ManualResetEventSlim(false);
        _git.AmendStagedGate = gate;

        _vm.SetAmend(true);

        // Entered amend synchronously, seeded from the index-vs-HEAD list; one cheap head read, no diff.
        Assert.True(_vm.Amend.Value);
        Assert.Equal(new[] { "b.txt" }, _vm.Staged.Value.Select(f => f.Path).ToArray());
        Assert.Equal(1, _git.GetHeadCommitMessageCalls);
        Assert.Equal(0, _git.GetAmendStagedFilesCalls);

        // Release the deferred diff and drain: the staged panel is replaced by the index-vs-HEAD^ view.
        gate.Set();
        DrainUntil(() => _vm.Staged.Value.Any(f => f.Path == "a.txt"), "the deferred amend diff to land");

        Assert.Equal(1, _git.GetAmendStagedFilesCalls);
        var staged = _vm.Staged.Value.Select(f => f.Path).OrderBy(p => p).ToArray();
        Assert.Equal(new[] { "a.txt", "b.txt" }, staged);
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
        try { ForceDelete(new DirectoryInfo(_root)); } catch { /* best effort */ }
    }

    private static void ForceDelete(DirectoryInfo dir)
    {
        if (!dir.Exists) return;
        foreach (var file in dir.GetFiles("*", SearchOption.AllDirectories))
            file.Attributes = FileAttributes.Normal;
        dir.Delete(recursive: true);
    }
}
