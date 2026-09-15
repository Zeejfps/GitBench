using GitBench.Features.Notifications;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Editor;

/// <summary>Stages the file the reader just saved, which is how git records that a conflict is over.</summary>
internal sealed class MarkResolvedDialogViewModel
{
    private readonly Repo _repo;
    private readonly string _relativePath;
    private readonly IGitConflictOperations _conflicts;
    private readonly IMessageBus _bus;
    private readonly ILocalizationService _loc;
    private readonly Action _onClose;

    public AsyncCommand MarkResolved { get; }

    public MarkResolvedDialogViewModel(
        Repo repo,
        string relativePath,
        IGitConflictOperations conflicts,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        ILocalizationService loc,
        Action onClose)
    {
        _repo = repo;
        _relativePath = relativePath;
        _conflicts = conflicts;
        _bus = bus;
        _loc = loc;
        _onClose = onClose;

        MarkResolved = AsyncCommand.ForOutcome(dispatcher, Resolve, OnResolved);
    }

    private GitOutcome Resolve() => _conflicts.MarkResolved(_repo, _relativePath);

    private void OnResolved()
    {
        MutationEffects.Index(_bus, _repo.Id, _relativePath).Broadcast();
        _onClose();
        _bus.Broadcast(new ShowToastMessage(
            ToastIntent.Success(_loc.Strings.Value.EditorMarkResolvedToast)));
    }
}
