using ZGF.Observable;

namespace GitGui;

internal sealed class RebaseBranchDialogViewModel : IDisposable
{
    public State<bool> Autostash { get; } = new(false);
    public State<RebasePreviewState> PreviewState { get; } = new(RebasePreviewState.Unknown);

    public AsyncCommand Rebase { get; }

    public event Action? CloseRequested;

    public RebaseBranchDialogViewModel(
        RebaseBranchRequest request,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        Rebase = new AsyncCommand(
            dispatcher,
            work: () =>
            {
                var outcome = gitService.Rebase(request.Repo, request.TargetRef, Autostash.Value);
                return outcome.Success ? null : (outcome.ErrorMessage ?? "Rebase failed.");
            },
            onSuccess: () =>
            {
                bus.Broadcast(new RefsChangedMessage(request.Repo.Id));
                bus.Broadcast(new WorkingTreeChangedMessage(request.Repo.Id));
                CloseRequested?.Invoke();
            });

        StartPreview(request, gitService, dispatcher);
    }

    private void StartPreview(RebaseBranchRequest request, IGitService gitService, IUiDispatcher dispatcher)
    {
        Task.Run(() =>
        {
            RebasePreviewResult result;
            try { result = gitService.PreviewRebase(request.Repo, request.TargetRef); }
            catch (Exception ex) { result = new RebasePreviewResult(RebasePreviewState.Unknown, ex.Message); }

            dispatcher.Post(() => PreviewState.Value = result.State);
        });
    }

    public void Dispose() { }
}

internal readonly record struct RebaseBranchRequest(
    Repo Repo,
    string SourceBranch,
    string TargetRef,
    string TargetDisplay);
