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

internal sealed record ForcePushDialog : Widget
{
    public required Repo Repo { get; init; }
    public required string BranchName { get; init; }
    public required int Ahead { get; init; }
    public required int Behind { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var repo = Repo;
        var onClose = OnClose;
        var gitService = ctx.Require<IGitRemoteOperations>();
        var bus = ctx.Require<IMessageBus>();

        var forcePush = AsyncCommand.ForOutcome(
            ctx.Require<IUiDispatcher>(),
            work: () => gitService.Push(repo, force: true),
            onSuccess: () =>
            {
                bus.Broadcast(new RefsChangedMessage(repo.Id));
                onClose();
            });

        var s = ctx.Localization().Strings.Value;
        var displayBranch = string.IsNullOrEmpty(BranchName) ? s.BranchesForcePushThisBranch : $"'{BranchName}'";

        return new Dialog
        {
            Title = s.BranchesForcePushTitle,
            OnClose = onClose,
            Action = (s.BranchesForcePushAction, DialogButtonRole.Destructive),
            Command = forcePush,
            ConfirmKeys = true,
            Body =
            [
                new Text
                {
                    Value = s.BranchesForcePushDescription(displayBranch, Ahead, Behind),
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.BodyText),
                },
            ],
        };
    }
}
