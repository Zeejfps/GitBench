using System.Collections.Concurrent;
using GitBench.Features.Worktrees;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

// The create dialog opens on a path the user can accept as-is: a sibling of the repo named after
// the branch being created, tracking the branch field until the user edits the path themselves.
// Submodules initialize by default, so a worktree of a superproject is usable on arrival.
public sealed class CreateWorktreeDialogViewModelTests
{
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly MessageBus _bus = new();
    private readonly LocalizationService _loc = new(new State<Locale>(Locale.En));
    private readonly RecordingWorktrees _git = new();
    private readonly Repo _primary = new(Guid.NewGuid(), Path.Combine(Root, "repos", "app"), "app");

    private static readonly string Root = OperatingSystem.IsWindows() ? "C:\\" : "/";

    private CreateWorktreeDialogViewModel Vm(params string[] existingDirectories)
        => new(new CreateWorktreeRequest(_primary), _git, _dispatcher, _bus, _loc,
            p => existingDirectories.Contains(p));

    private static readonly string Parent = Path.Combine(Root, "repos");

    private static string Sibling(string name) => Path.Combine(Parent, name);

    [Fact]
    public void TheParentFolderDefaultsToTheRepositorysOwn()
    {
        var vm = Vm();
        Assert.Equal(Parent, vm.ParentDir.Value);
        Assert.Equal("app-worktree", vm.FolderName.Value);
    }

    [Fact]
    public void TypingABranchNameNamesTheFolderAfterIt()
    {
        var vm = Vm();
        vm.NewBranchName.Value = "feature/login";
        Assert.Equal("app-feature-login", vm.FolderName.Value);
        Assert.Equal(Parent, vm.ParentDir.Value);
    }

    [Fact]
    public void AManuallyEditedFolderNameIsNotOverwritten()
    {
        var vm = Vm();
        vm.FolderName.Value = "somewhere-else";
        vm.NewBranchName.Value = "feature";
        Assert.Equal("somewhere-else", vm.FolderName.Value);
    }

    [Fact]
    public void AnOccupiedFolderNameGetsACounter()
    {
        var vm = Vm(Sibling("app-worktree"), Sibling("app-worktree-2"));
        Assert.Equal("app-worktree-3", vm.FolderName.Value);
    }

    // The two fields are one path by the time git sees them.
    [Fact]
    public void TheParentAndFolderNameCombineIntoTheWorktreePath()
    {
        var vm = Vm();
        vm.NewBranchName.Value = "feature";

        Run(vm);

        Assert.Equal(Sibling("app-feature"), _git.Request!.Path);
    }

    // Browse opens where the field points, and the field points at a directory that does not
    // exist yet by design — so the picker has to climb to the closest one that does.
    [Fact]
    public void BrowseOpensAtTheClosestExistingFolderAboveTheTypedPath()
    {
        var typed = Sibling("app-feature");

        Assert.Equal(Parent, WorktreePathDefaults.NearestExistingDirectory(typed, p => p == Parent));
        Assert.Equal(typed, WorktreePathDefaults.NearestExistingDirectory(typed, p => p == typed));
        Assert.Null(WorktreePathDefaults.NearestExistingDirectory(typed, _ => false));
        Assert.Null(WorktreePathDefaults.NearestExistingDirectory("  ", _ => true));
    }

    [Fact]
    public void SubmodulesInitializeRecursivelyByDefault()
    {
        var vm = Vm();
        Assert.True(vm.InitSubmodules.Value);
        Assert.True(vm.RecurseSubmodules.Value);

        Run(vm);

        Assert.NotNull(_git.Request);
        Assert.True(_git.Request!.InitSubmodules);
        Assert.True(_git.Request!.RecurseSubmodules);
    }

    [Fact]
    public void ClearingTheCheckboxesLeavesSubmodulesAlone()
    {
        var vm = Vm();
        vm.InitSubmodules.Value = false;
        vm.RecurseSubmodules.Value = false;

        Run(vm);

        Assert.False(_git.Request!.InitSubmodules);
        Assert.False(_git.Request!.RecurseSubmodules);
    }

    // A worktree whose submodules failed to initialize is still a worktree: the dialog closes and
    // refreshes, and git's complaint arrives as a warning rather than holding the dialog open.
    [Fact]
    public void AFailedSubmoduleStepClosesTheDialogAndWarns()
    {
        _git.Outcome = new WorktreeAddOutcome.Added("fatal: could not read Username");
        var refreshed = new List<WorktreesChangedMessage>();
        var errors = new List<ShowOperationErrorMessage>();
        _bus.Subscribe<WorktreesChangedMessage>(refreshed.Add);
        _bus.Subscribe<ShowOperationErrorMessage>(errors.Add);

        var vm = Vm();
        var closed = false;
        vm.CloseRequested += () => closed = true;
        Run(vm);

        Assert.True(closed);
        Assert.Single(refreshed);
        Assert.Null(vm.Create.Error.Value);
        Assert.Equal("fatal: could not read Username", Assert.Single(errors).Message);
    }

    private void Run(CreateWorktreeDialogViewModel vm)
    {
        vm.Create.Execute();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            _dispatcher.Drain();
            if (!vm.Create.IsRunning.Value) break;
            Thread.Sleep(5);
        }
        Assert.False(vm.Create.IsRunning.Value, "the create command never completed");
    }

    private sealed class RecordingWorktrees : IGitWorktreeOperations
    {
        public WorktreeAddOutcome Outcome = WorktreeAddOutcome.Ok;
        public WorktreeAddRequest? Request;

        public IReadOnlyList<WorktreeInfo> ListWorktrees(Repo primary) => Array.Empty<WorktreeInfo>();

        public WorktreeAddOutcome AddWorktree(Repo primary, WorktreeAddRequest request)
        {
            Request = request;
            return Outcome;
        }

        public WorktreeRemoveOutcome RemoveWorktree(Repo primary, string worktreePath, bool force) => WorktreeRemoveOutcome.Ok;
        public GitOutcome UnlockWorktree(Repo primary, string worktreePath) => GitOutcome.Ok;
        public GitOutcome PruneWorktrees(Repo primary) => GitOutcome.Ok;
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
