using ZGF.Observable;

namespace GitBench;

internal sealed class DeleteLocalBranchDialogViewModel : IDisposable
{
    private readonly DeleteLocalBranchRequest _request;
    private readonly IGitService _gitService;
    private readonly IMessageBus _bus;
    // Plain field, not a State<T>: it carries the remote-delete outcome out of the background
    // work lambda into the UI-thread OnDeleteSucceeded callback. The work runs on a worker
    // thread and completes before AsyncCommand posts the callback, so the read sees the write —
    // and a plain assignment fires no observable notifications on the wrong thread.
    private DeleteRemoteBranchOutcome? _partialFailure;

    public State<bool> Force { get; } = new(false);
    public State<bool> DeleteRemote { get; } = new(false);
    public bool HasUpstream { get; }

    public AsyncCommand Delete { get; }

    public event Action? CloseRequested;

    public DeleteLocalBranchDialogViewModel(
        DeleteLocalBranchRequest request,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        _request = request;
        _gitService = gitService;
        _bus = bus;

        HasUpstream = !string.IsNullOrEmpty(request.UpstreamRemote)
                      && !string.IsNullOrEmpty(request.UpstreamBranch);

        Delete = new AsyncCommand(dispatcher, DoDelete, OnDeleteSucceeded);
    }

    private string? DoDelete()
    {
        var force = Force.Value;
        var deleteRemote = DeleteRemote.Value && HasUpstream;
        var remoteName = _request.UpstreamRemote;
        var remoteBranch = _request.UpstreamBranch;

        var local = _gitService.DeleteBranch(_request.Repo, _request.BranchName, force);
        if (!local.Success)
            return local.ErrorMessage ?? "Delete failed.";

        if (deleteRemote)
        {
            DeleteRemoteBranchOutcome remote;
            try { remote = _gitService.DeleteRemoteBranch(_request.Repo, remoteName!, remoteBranch!); }
            catch (Exception ex) { remote = new DeleteRemoteBranchOutcome(false, ex.Message); }
            if (!remote.Success)
                _partialFailure = remote;
        }
        return null;
    }

    private void OnDeleteSucceeded()
    {
        _bus.Broadcast(new RefsChangedMessage(_request.Repo.Id));
        CloseRequested?.Invoke();

        if (_partialFailure is { } failed)
        {
            var remoteName = _request.UpstreamRemote;
            var remoteBranch = _request.UpstreamBranch;
            _bus.Broadcast(new ShowOperationErrorMessage(
                "Remote delete failed",
                failed.ErrorMessage
                    ?? $"Local branch deleted, but failed to delete '{remoteBranch}' on '{remoteName}'."));
        }
    }

    public void Dispose() { }
}

internal readonly record struct DeleteLocalBranchRequest(
    Repo Repo,
    string BranchName,
    string? UpstreamRemote = null,
    string? UpstreamBranch = null);
