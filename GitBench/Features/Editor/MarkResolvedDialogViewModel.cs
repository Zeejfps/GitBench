using GitBench.Features.Notifications;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Editor;

/// <summary>Stages the file the reader just saved, which is how git records that a conflict is over.</summary>
internal sealed class MarkResolvedDialogViewModel : IDialogViewModel
{
    private readonly Repo _repo;
    private readonly string _relativePath;
    private readonly IGitConflictOperations _conflicts;
    private readonly IMessageBus _bus;
    private readonly ILocalizationService _loc;

    public AsyncCommand MarkResolved { get; }

    public event Action? CloseRequested;

    public MarkResolvedDialogViewModel(
        Repo repo,
        string relativePath,
        IGitConflictOperations conflicts,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        ILocalizationService loc)
    {
        _repo = repo;
        _relativePath = relativePath;
        _conflicts = conflicts;
        _bus = bus;
        _loc = loc;

        MarkResolved = AsyncCommand.ForOutcome(dispatcher, Resolve, OnResolved);
    }

    private GitOutcome Resolve() => _conflicts.MarkResolved(_repo, _relativePath);

    private void OnResolved()
    {
        MutationEffects.Index(_bus, _repo.Id, _relativePath).Broadcast();
        CloseRequested?.Invoke();
        _bus.Broadcast(new ShowToastMessage(
            ToastIntent.Success(_loc.Strings.Value.EditorMarkResolvedToast)));
    }

    public void Dispose() { }
}
