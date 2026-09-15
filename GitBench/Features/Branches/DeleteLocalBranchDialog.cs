using GitBench.Controls.Dialogs;
using GitBench.Features.Notifications;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Branches;

internal sealed record DeleteLocalBranchDialog : Widget
{
    public required Repo Repo { get; init; }
    public required string BranchName { get; init; }
    public string? UpstreamRemote { get; init; }
    public string? UpstreamBranch { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var repo = Repo;
        var branchName = BranchName;
        (string Remote, string Branch)? upstream =
            UpstreamRemote is { Length: > 0 } remoteName && UpstreamBranch is { Length: > 0 } remoteBranch
                ? (remoteName, remoteBranch)
                : null;
        var onClose = OnClose;
        var gitService = ctx.Require<IGitBranchOperations>();
        var bus = ctx.Require<IMessageBus>();
        var loc = ctx.Localization();

        var force = new State<bool>(false);
        var deleteRemote = new State<bool>(false);

        // Plain local, not a State<T>: it carries the remote-delete outcome out of the background
        // work lambda into the UI-thread success callback. The work runs on a worker thread and
        // completes before AsyncCommand posts the callback, so the read sees the write — and a
        // plain assignment fires no observable notifications on the wrong thread.
        GitOutcome.Failed? partialFailure = null;

        var delete = AsyncCommand.ForOutcome(
            ctx.Require<IUiDispatcher>(),
            work: () =>
            {
                if (gitService.DeleteBranch(repo, branchName, force.Value) is GitOutcome.Failed local)
                    return local;

                if (deleteRemote.Value && upstream is { } target)
                {
                    GitOutcome remote;
                    try { remote = gitService.DeleteRemoteBranch(repo, target.Remote, target.Branch); }
                    catch (Exception ex) { remote = new GitOutcome.Failed(ex.Message); }
                    if (remote is GitOutcome.Failed failed)
                        partialFailure = failed;
                }
                return GitOutcome.Ok;
            },
            onSuccess: () =>
            {
                bus.Broadcast(new RefsChangedMessage(repo.Id));
                onClose();

                if (partialFailure is { } failed)
                {
                    bus.Broadcast(new ShowOperationErrorMessage("Remote delete failed", failed.Message));
                    return;
                }

                bus.Broadcast(new ShowToastMessage(ToastIntent.Success(loc.Strings.Value.ToastBranchDeleted)));
            });

        var s = loc.Strings.Value;
        var body = new List<IWidget>
        {
            new DialogBodyText { Value = s.BranchesDeleteLocalTitle(BranchName) },
            new CheckboxWidget
            {
                Label = s.BranchesDeleteLocalForceLabel,
                Checked = force,
                Height = Sizes.RowHeight,
            }.WithController<KbmController>(),
            new Text
            {
                Value = s.BranchesDeleteLocalForceHint,
                Wrap = TextWrap.Wrap,
                Color = Theme.Color(t => t.DialogBody.RowTextMissing),
            },
        };
        if (upstream is { } up)
        {
            body.Add(new CheckboxWidget
            {
                Label = s.BranchesDeleteLocalRemoteLabel(up.Branch, up.Remote),
                Checked = deleteRemote,
                Height = Sizes.RowHeight,
            }.WithController<KbmController>());
        }

        return new Dialog
        {
            Title = s.BranchesDeleteLocalDialogTitle,
            OnClose = onClose,
            Action = (s.CommonDelete, DialogButtonRole.Destructive),
            Command = delete,
            ConfirmKeys = true,
            Body = body.ToArray(),
        };
    }
}
