using System.Diagnostics;
using GitBench.App;
using GitBench.Features.Branches;
using GitBench.Features.Commits;
using GitBench.Features.Editor;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

/// <summary>The destructive git flows that rewrite a working tree from a dialog, each asked about before it runs.</summary>
public sealed class UnsavedEditsGuardedFlowsTests : IDisposable
{
    private readonly string _root;
    private readonly RepoRegistry _registry;
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly MessageBus _bus = new();
    private readonly LocalizationService _loc = new(new State<Locale>(Locale.En));
    private readonly GitService _git = new(new RepoActivityTracker());
    private readonly DocumentStore _documents;
    private readonly List<ShowDialogMessage> _dialogs = [];
    private readonly string _repoPath;
    private readonly Repo _repo;

    public UnsavedEditsGuardedFlowsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gitbench-guarded-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var statePath = Path.Combine(_root, "repos.json");
        _registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
        _documents = new DocumentStore(_registry, _loc);
        _documents.Start();
        _bus.Subscribe<ShowDialogMessage>(_dialogs.Add);

        _repoPath = Path.Combine(_root, "solo");
        Directory.CreateDirectory(_repoPath);
        TestGit.Init(_repoPath);
        File.WriteAllText(Path.Combine(_repoPath, "a.txt"), "0");
        Git("add", "a.txt");
        Git("commit", "-qm", "base");
        Git("branch", "old");
        File.WriteAllText(Path.Combine(_repoPath, "a.txt"), "1");
        Git("commit", "-qam", "second");
        Assert.Equal(OpenRepoOutcome.Opened, _registry.Open(_repoPath));
        _repo = _registry.Repos.Single();
    }

    [Fact]
    public void ForceMovingABranchOntoACommitAsksBeforeRewritingATypedFile()
    {
        TypeInto("a.txt");
        var before = Sha("old");

        MoveDialog().Move();
        Drain();

        Assert.Single(_dialogs);
        Assert.Equal(before, Sha("old"));
    }

    [Fact]
    public void ForceMovingABranchRunsUnaskedWhenNothingIsHalfTyped()
    {
        MoveDialog().Move();
        DrainUntil(() => Sha("old") == Sha("HEAD"), "the branch to move");

        Assert.Empty(_dialogs);
    }

    [Fact]
    public void CheckingOutARemoteBranchAsksAboutTheWholeTreeAndOpensNothingUntilAnswered()
    {
        var guard = new PendingUnsavedEditsGuard();
        using var branches = Branches(guard);

        branches.ActivateRemoteBranch("origin", "origin/feature");

        Assert.Equal([new OverwriteScope.WorkingTree(_repo.Id)], guard.Asked);
        Assert.Empty(_dialogs);

        guard.Accept();
        Assert.Single(_dialogs);
    }

    [Fact]
    public void CheckingOutARemoteBranchOpensTheDialogUnaskedWhenNothingIsHalfTyped()
    {
        using var branches = Branches(RealGuard());

        branches.ActivateRemoteBranch("origin", "origin/feature");

        Assert.Single(_dialogs);
    }

    [Fact]
    public void CreatingABranchFromACommitAndCheckingItOutAsksFirst()
    {
        TypeInto("a.txt");
        var guard = new PendingUnsavedEditsGuard();
        var vm = CreateDialog(guard);
        vm.StartPoint.Value = Sha("HEAD");

        vm.Create.Execute();

        Assert.Equal([new OverwriteScope.WorkingTree(_repo.Id)], guard.Asked);
        Assert.False(HasBranch("topic"));

        guard.Accept();
        _dispatcher.Drain();
        DrainUntil(() => HasBranch("topic"), "the branch to be created");
    }

    [Fact]
    public void CreatingABranchFromHeadNeverAsks()
    {
        TypeInto("a.txt");
        var guard = new PendingUnsavedEditsGuard();
        var vm = CreateDialog(guard);

        vm.Create.Execute();
        _dispatcher.Drain();

        Assert.Empty(guard.Asked);
        DrainUntil(() => HasBranch("topic"), "the branch to be created");
    }

    [Fact]
    public void CreatingABranchWithoutCheckingItOutNeverAsks()
    {
        TypeInto("a.txt");
        var guard = new PendingUnsavedEditsGuard();
        var vm = CreateDialog(guard);
        vm.StartPoint.Value = Sha("HEAD");
        vm.Checkout.Value = false;

        vm.Create.Execute();
        _dispatcher.Drain();

        Assert.Empty(guard.Asked);
        DrainUntil(() => HasBranch("topic"), "the branch to be created");
    }

    [Fact]
    public void RemovingARepositoryNamesTheFilesItsRemovalWouldDiscard()
    {
        TypeInto("a.txt");

        Assert.Equal(["a.txt"], RepoRemoval.UnsavedFiles(_registry, _documents, _repo.Id));
    }

    [Fact]
    public void RemovingACleanRepositoryHasNothingToName()
        => Assert.Empty(RepoRemoval.UnsavedFiles(_registry, _documents, _repo.Id));

    [Fact]
    public void RemovingAPrimaryNamesTheFilesOfTheRowsItTakesWithIt()
    {
        var worktreePath = Path.Combine(_root, "wt");
        Directory.CreateDirectory(worktreePath);
        _registry.ReplaceWorktreesFor(
            _repo.Id, [new WorktreeDescriptor(worktreePath, "wt", "feature")]);
        var worktree = _registry.Repos.Single(r => r.ParentRepoId == _repo.Id);

        File.WriteAllText(Path.Combine(worktreePath, "b.txt"), "0");
        TypeInto(worktree.Id, Path.Combine(worktreePath, "b.txt"));

        Assert.Equal(["b.txt"], RepoRemoval.UnsavedFiles(_registry, _documents, _repo.Id));
    }

    private MoveBranchDialogViewModel MoveDialog() => new(
        _repo,
        "old",
        Sha("HEAD"),
        _git,
        new RepoHeadStore(_git, _bus, _loc, _dispatcher, RealGuard()),
        _loc,
        () => { });

    private BranchesViewModel Branches(IUnsavedEditsGuard guard)
    {
        _registry.SetActive(_repo.Id);
        return new BranchesViewModel(
            _registry,
            _git,
            _git,
            _dispatcher,
            _bus,
            new State<MainViewMode>(MainViewMode.LocalChanges),
            new FakeContentNavigator(),
            new FakeSnapshotStore(),
            new IdleStatusStore(),
            new IdleRemoteOperations(),
            new RepoHeadStore(_git, _bus, _loc, _dispatcher, guard),
            new ManualTicker(),
            _loc,
            guard);
    }

    private IUnsavedEditsGuard RealGuard() => new UnsavedEditsGuard(_documents, _registry, _bus);

    private CreateBranchDialogViewModel CreateDialog(IUnsavedEditsGuard guard) => new(
        _repo,
        GitRef.Head,
        "main",
        "topic",
        _git,
        _dispatcher,
        _bus,
        new RepoHeadStore(_git, _bus, _loc, _dispatcher, RealGuard()),
        _loc,
        () => { },
        guard);

    private void TypeInto(string name) => TypeInto(_repo.Id, Path.Combine(_repoPath, name));

    private void TypeInto(Guid repoId, string path)
    {
        var text = File.ReadAllText(path);
        var buffer = _documents.For(repoId).Open(
            path,
            new FileText(text),
            new FileWriteBack.Reversible(new FileEncoding(FileCharset.Utf8, LineEnding.Lf, EndsWithNewline: false)),
            null);
        buffer!.Session.Type(SelectionRange.At(TextPosition.At(1, 0)), "x");
    }

    private string Sha(string rev) => Git("rev-parse", rev).Trim();

    private bool HasBranch(string name) => Git("branch", "--list", name).Trim().Length > 0;

    private string Git(params string[] args) => TestGit.Run(_repoPath, args);

    private void Drain()
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(1))
        {
            _dispatcher.Drain();
            Thread.Sleep(10);
        }
        _dispatcher.Drain();
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

    public void Dispose()
    {
        _documents.Dispose();
        _registry.Dispose();
        _loc.Dispose();
        DirectoryTree.Delete(_root);
    }

    private sealed class PendingUnsavedEditsGuard : IUnsavedEditsGuard
    {
        private Action? _pending;

        public List<OverwriteScope> Asked { get; } = [];

        public void Guard(OverwriteScope scope, Action proceed)
        {
            Asked.Add(scope);
            _pending = proceed;
        }

        public void Accept()
        {
            var proceed = _pending;
            _pending = null;
            proceed?.Invoke();
        }
    }

    private sealed class IdleStatusStore : IRepoStatusStore
    {
        private readonly State<RepoStatus> _active = new(RepoStatus.Unknown);

        public IReadable<RepoStatus> Active => _active;
        public RepoStatus For(Guid repoId) => RepoStatus.Unknown;
    }
}
