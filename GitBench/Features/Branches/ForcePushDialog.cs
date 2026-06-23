using GitBench.Controls.Dialogs;
using GitBench.Git;
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
        var vm = new ForcePushDialogViewModel(
            Repo,
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>());

        var s = ctx.Localization().Strings.Value;
        var displayBranch = string.IsNullOrEmpty(BranchName) ? s.BranchesForcePushThisBranch : $"'{BranchName}'";

        return new Dialog
        {
            Title = s.BranchesForcePushTitle,
            OnClose = OnClose,
            ViewModel = vm,
            Action = (s.BranchesForcePushAction, DialogButtonRole.Destructive),
            Command = vm.ForcePush,
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
