using GitBench.Controls.Dialogs;
using GitBench.Git;
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
        var vm = new ForcePushDialogViewModel(
            Repo,
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>());

        var displayBranch = string.IsNullOrEmpty(BranchName) ? "this branch" : $"'{BranchName}'";

        return new Dialog
        {
            Title = "Force push?",
            OnClose = OnClose,
            ViewModel = vm,
            Action = ("Force push", DialogButtonRole.Destructive),
            Command = vm.ForcePush,
            ConfirmKeys = true,
            Body =
            [
                new Text
                {
                    Value = $"{displayBranch} has diverged from its upstream — {Ahead} ahead, {Behind} behind. "
                          + "A regular push will be rejected. Force-push (with lease) will overwrite the remote "
                          + "branch with your local history; any commits on the remote that you haven't fetched "
                          + "will be lost. The lease refuses the push if the remote moved since your last fetch.",
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(s => s.DialogBody.BodyText),
                },
            ],
        };
    }
}
