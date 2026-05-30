using ZGF.Observable;

namespace GitGui;

internal sealed class MergeBranchDialogViewModel : IDisposable
{
    public State<MergeStrategy> Strategy { get; } = new(MergeStrategy.Default);
    public State<MergePreviewState> PreviewState { get; } = new(MergePreviewState.Unknown);

    public AsyncCommand Merge { get; }

    public event Action? CloseRequested;

    public MergeBranchDialogViewModel(
        MergeBranchRequest request,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        Merge = new AsyncCommand(
            dispatcher,
            work: () =>
            {
                var outcome = gitService.Merge(request.Repo, request.SourceRef, Strategy.Value);
                return outcome.Success ? null : (outcome.ErrorMessage ?? "Merge failed.");
            },
            onSuccess: () =>
            {
                bus.Broadcast(new RefsChangedMessage(request.Repo.Id));
                bus.Broadcast(new WorkingTreeChangedMessage(request.Repo.Id));
                CloseRequested?.Invoke();
            });

        StartPreview(request, gitService, dispatcher);
    }

    private void StartPreview(MergeBranchRequest request, IGitService gitService, IUiDispatcher dispatcher)
    {
        Task.Run(() =>
        {
            MergePreviewResult result;
            try { result = gitService.PreviewMerge(request.Repo, request.SourceRef); }
            catch (Exception ex) { result = new MergePreviewResult(MergePreviewState.Unknown, ex.Message); }

            dispatcher.Post(() => PreviewState.Value = result.State);
        });
    }

    public void Dispose() { }
}

internal readonly record struct MergeBranchRequest(
    Repo Repo,
    string SourceRef,
    string SourceDisplay,
    string TargetBranch);
