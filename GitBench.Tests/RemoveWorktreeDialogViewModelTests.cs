using System.Collections.Concurrent;
using GitBench.Features.Notifications;
using GitBench.Features.Repos;
using GitBench.Features.Worktrees;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Platform;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

// Removing a worktree whose directory git couldn't finish deleting is not a failed removal: the
// worktree is gone, and only files are left. The dialog has to close on it like a success — the
// leftovers are a cleanup note, not an error — while a removal git actually refused still holds
// the dialog open.
public sealed class RemoveWorktreeDialogViewModelTests
{
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly MessageBus _bus = new();
    private readonly LocalizationService _loc = new(new State<Locale>(Locale.En));
    private readonly ScriptedWorktrees _git = new();
    private readonly RecordingShell _shell = new();
    private readonly Repo _primary = new(Guid.NewGuid(), "C:/repos/app", "app");
    private readonly Repo _worktree = new(Guid.NewGuid(), "C:/repos/app-feature", "app-feature");

    private (RemoveWorktreeDialogViewModel Vm, bool Closed) Run()
    {
        var vm = new RemoveWorktreeDialogViewModel(
            new RemoveWorktreeRequest(_primary, _worktree), _git, _dispatcher, _bus, _shell, _loc);
        var closed = false;
        vm.CloseRequested += () => closed = true;

        vm.Remove.Execute();
        // Drain until the command settles rather than until something is queued: under a loaded
        // thread pool the work can take a while to start, and a fixed wait makes the test a coin flip.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            _dispatcher.Drain();
            if (!vm.Remove.IsRunning.Value) break;
            Thread.Sleep(5);
        }

        Assert.False(vm.Remove.IsRunning.Value, "the remove command never completed");
        return (vm, closed);
    }

    [Fact]
    public void LeftoverFilesCloseTheDialogAndWarnInsteadOfFailing()
    {
        _git.Outcome = new WorktreeRemoveOutcome.RemovedWithLeftovers(_worktree.Path, "Access to the path is denied.");
        var refreshed = new List<WorktreesChangedMessage>();
        var toasts = new List<ShowToastMessage>();
        _bus.Subscribe<WorktreesChangedMessage>(refreshed.Add);
        _bus.Subscribe<ShowToastMessage>(toasts.Add);

        var (vm, closed) = Run();

        Assert.True(closed);
        Assert.Null(vm.Remove.Error.Value);
        Assert.Equal(_primary.Id, Assert.Single(refreshed).PrimaryRepoId);

        var toast = Assert.Single(toasts).Intent;
        Assert.Equal(ToastSeverity.Warning, toast.Severity);
        Assert.Contains(_worktree.Path, toast.Message);
        Assert.Contains("Access to the path is denied.", toast.Message);

        toast.Action!.Invoke();
        Assert.Equal(_worktree.Path, _shell.Opened);
    }

    [Fact]
    public void ACleanRemovalSaysNothing()
    {
        _git.Outcome = WorktreeRemoveOutcome.Ok;
        var toasts = new List<ShowToastMessage>();
        _bus.Subscribe<ShowToastMessage>(toasts.Add);

        var (vm, closed) = Run();

        Assert.True(closed);
        Assert.Null(vm.Remove.Error.Value);
        Assert.Empty(toasts);
    }

    [Fact]
    public void ARefusedRemovalKeepsTheDialogOpenAndDoesNotRefresh()
    {
        _git.Outcome = new WorktreeRemoveOutcome.Failed("fatal: contains modified or untracked files");
        var refreshed = new List<WorktreesChangedMessage>();
        var toasts = new List<ShowToastMessage>();
        _bus.Subscribe<WorktreesChangedMessage>(refreshed.Add);
        _bus.Subscribe<ShowToastMessage>(toasts.Add);

        var (vm, closed) = Run();

        Assert.False(closed);
        Assert.Equal("fatal: contains modified or untracked files", vm.Remove.Error.Value);
        Assert.Empty(refreshed);
        Assert.Empty(toasts);
    }

    private sealed class ScriptedWorktrees : IGitWorktreeOperations
    {
        public WorktreeRemoveOutcome Outcome = WorktreeRemoveOutcome.Ok;

        public IReadOnlyList<WorktreeInfo> ListWorktrees(Repo primary) => Array.Empty<WorktreeInfo>();
        public WorktreeAddOutcome AddWorktree(Repo primary, WorktreeAddRequest request) => WorktreeAddOutcome.Ok;
        public WorktreeRemoveOutcome RemoveWorktree(Repo primary, string worktreePath, bool force) => Outcome;
        public GitOutcome UnlockWorktree(Repo primary, string worktreePath) => GitOutcome.Ok;
        public GitOutcome PruneWorktrees(Repo primary) => GitOutcome.Ok;
    }

    private sealed class RecordingShell : IPlatformShell
    {
        public string? Opened;

        public void OpenFolder(string path) => Opened = path;
        public void OpenTerminal(string path) { }
        public void OpenFile(string path) { }
        public void OpenUrl(string url) { }
    }

    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly ConcurrentQueue<Action> _queue = new();

        public int Queued => _queue.Count;

        public void Post(Action action) => _queue.Enqueue(action);

        public void Drain()
        {
            while (_queue.TryDequeue(out var action)) action();
        }
    }
}
