using GitBench.Controls.Dialogs;
using GitBench.Git;
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
        var vm = new DeleteLocalBranchDialogViewModel(
            new DeleteLocalBranchRequest(Repo, BranchName, UpstreamRemote, UpstreamBranch),
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>());

        var body = new List<IWidget>
        {
            new Text
            {
                Value = $"Delete local branch '{BranchName}'?",
                Wrap = TextWrap.Wrap,
                Color = Theme.Color(s => s.DialogBody.BodyText),
            },
            new CheckboxWidget
            {
                Label = "Delete even if not merged",
                Checked = vm.Force,
                Height = 22,
            }.WithController<KbmController>(),
            new Text
            {
                Value = "Unchecked: refuses if the branch isn't fully merged into its upstream or HEAD.",
                Wrap = TextWrap.Wrap,
                Color = Theme.Color(s => s.DialogBody.RowTextMissing),
            },
        };
        if (vm.HasUpstream)
        {
            body.Add(new CheckboxWidget
            {
                Label = $"Also delete '{UpstreamBranch}' on '{UpstreamRemote}'",
                Checked = vm.DeleteRemote,
                Height = 22,
            }.WithController<KbmController>());
        }

        return new Dialog
        {
            Title = "Delete branch",
            OnClose = OnClose,
            ViewModel = vm,
            Action = ("Delete", DialogButtonRole.Destructive),
            Command = vm.Delete,
            ConfirmKeys = true,
            Body = body.ToArray(),
        };
    }
}
