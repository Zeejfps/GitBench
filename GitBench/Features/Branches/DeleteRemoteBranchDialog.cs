using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Branches;

/// <summary>
/// Confirmation modal for deleting a branch from a remote. Calls
/// `git push &lt;remote&gt; --delete &lt;branch&gt;` — a network operation that doesn't touch
/// local branches. The server may refuse for protected refs; that error is surfaced.
/// </summary>
internal sealed record DeleteRemoteBranchDialog : Widget
{
    public required Repo Repo { get; init; }
    public required string RemoteName { get; init; }
    public required string BranchName { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var repo = Repo;
        var remoteName = RemoteName;
        var branchName = BranchName;
        var onClose = OnClose;
        var gitService = ctx.Require<IGitBranchOperations>();
        var bus = ctx.Require<IMessageBus>();

        var delete = AsyncCommand.ForOutcome(
            ctx.Require<IUiDispatcher>(),
            work: () => gitService.DeleteRemoteBranch(repo, remoteName, branchName),
            onSuccess: () =>
            {
                bus.Broadcast(new RefsChangedMessage(repo.Id));
                onClose();
            });

        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            Title = s.BranchesDeleteRemoteTitle,
            OnClose = onClose,
            Action = (s.CommonDelete, DialogButtonRole.Destructive),
            Command = delete,
            ConfirmKeys = true,
            Body =
            [
                new Text
                {
                    Value = s.BranchesDeleteRemoteDescription(BranchName, RemoteName),
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.BodyText),
                },
                new Text
                {
                    Value = s.BranchesDeleteRemoteInfo,
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.RowTextMissing),
                },
            ],
        };
    }
}
