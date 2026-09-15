using GitBench.Controls.Dialogs;
using GitBench.Features.Commits;
using GitBench.Features.LocalChanges;
using GitBench.Features.Notifications;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Stash;

internal sealed class StashDialogViewModel
{
    public CheckedFileList Files { get; }
    public IReadOnlySet<string> UntrackedPaths { get; }
    public State<string> Message { get; } = new(string.Empty);
    public State<bool> KeepStaged { get; } = new(false);
    public AsyncCommand Stash { get; }

    public StashDialogViewModel(
        Repo repo,
        LocalChangesSnapshot snapshot,
        IGitStashOperations gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        LocalChangesSelectionStore selectionStore,
        ILocalizationService loc,
        Action onClose)
    {
        var strings = loc.Strings.Value;
        var untracked = new HashSet<string>();
        // Unstaged first so the worktree status wins the display when a path appears on both sides.
        var seen = new Dictionary<string, FileChange>(snapshot.Staged.Count + snapshot.Unstaged.Count);
        foreach (var f in snapshot.Unstaged)
        {
            if (f.Status == FileChangeStatus.Added) untracked.Add(f.Path);
            seen[f.Path] = f;
        }
        foreach (var f in snapshot.Staged)
            seen.TryAdd(f.Path, f);

        UntrackedPaths = untracked;
        Files = new CheckedFileList(seen.Values, selectionStore.UnstagedPaths.Value, strings);

        Stash = AsyncCommand.ForOutcome(
            dispatcher,
            () =>
            {
                var paths = Files.CheckedInOrder();
                return gitService.CreateStash(repo, Message.Value, paths.Any(untracked.Contains), KeepStaged.Value, paths);
            },
            () =>
            {
                bus.Broadcast(new RefsChangedMessage(repo.Id));
                bus.Broadcast(new WorkingTreeChangedMessage(repo.Id));
                bus.Broadcast(new ShowToastMessage(ToastIntent.Success(strings.ToastChangesStashed)));
                onClose();
            },
            new Derived<bool>(() => Message.Value.Length > 0 && Files.CheckedPaths.Value.Count > 0));
    }
}
