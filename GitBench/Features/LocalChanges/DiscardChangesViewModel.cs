using GitBench.Controls.Dialogs;
using GitBench.Features.Notifications;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.LocalChanges;

internal sealed class DiscardChangesViewModel
{
    public CheckedFileList Files { get; }
    public AsyncCommand Discard { get; }

    public DiscardChangesViewModel(
        Repo repo,
        IReadOnlyList<string> paths,
        LocalChangesSnapshot snapshot,
        IGitWorkingTreeOperations gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        ILocalizationService loc,
        Action onClose)
    {
        var strings = loc.Strings.Value;
        Files = new CheckedFileList(snapshot.Unstaged, paths, strings);

        Discard = AsyncCommand.ForOutcome(
            dispatcher,
            () => gitService.DiscardChanges(repo, Files.CheckedInOrder()),
            () =>
            {
                bus.Broadcast(new WorkingTreeChangedMessage(repo.Id));
                bus.Broadcast(new ShowToastMessage(ToastIntent.Success(strings.ToastChangesDiscarded)));
                onClose();
            },
            new Derived<bool>(() => Files.CheckedPaths.Value.Count > 0));
    }
}
