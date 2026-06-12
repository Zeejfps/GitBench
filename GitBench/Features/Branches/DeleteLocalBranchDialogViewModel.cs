using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Branches;

internal sealed class DeleteLocalBranchDialogViewModel : IDialogViewModel
{
    private readonly DeleteLocalBranchRequest _request;
    private readonly IGitService _gitService;
    private readonly IMessageBus _bus;
    // Plain field, not a State<T>: it carries the remote-delete outcome out of the background
    // work lambda into the UI-thread OnDeleteSucceeded callback. The work runs on a worker
    // thread and completes before AsyncCommand posts the callback, so the read sees the write —
    // and a plain assignment fires no observable notifications on the wrong thread.
    private GitOutcome.Failed? _partialFailure;

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

        Delete = AsyncCommand.ForOutcome(dispatcher, DoDelete, OnDeleteSucceeded);
    }

    private GitOutcome DoDelete()
    {
        var force = Force.Value;
        var deleteRemote = DeleteRemote.Value && HasUpstream;
        var remoteName = _request.UpstreamRemote;
        var remoteBranch = _request.UpstreamBranch;

        if (_gitService.DeleteBranch(_request.Repo, _request.BranchName, force) is GitOutcome.Failed local)
            return local;

        if (deleteRemote)
        {
            GitOutcome remote;
            try { remote = _gitService.DeleteRemoteBranch(_request.Repo, remoteName!, remoteBranch!); }
            catch (Exception ex) { remote = new GitOutcome.Failed(ex.Message); }
            if (remote is GitOutcome.Failed failed)
                _partialFailure = failed;
        }
        return GitOutcome.Ok;
    }

    private void OnDeleteSucceeded()
    {
        _bus.Broadcast(new RefsChangedMessage(_request.Repo.Id));
        CloseRequested?.Invoke();

        if (_partialFailure is { } failed)
        {
            _bus.Broadcast(new ShowOperationErrorMessage(
                "Remote delete failed",
                failed.Message));
        }
    }

    public void Dispose() { }
}

internal readonly record struct DeleteLocalBranchRequest(
    Repo Repo,
    string BranchName,
    string? UpstreamRemote = null,
    string? UpstreamBranch = null);
