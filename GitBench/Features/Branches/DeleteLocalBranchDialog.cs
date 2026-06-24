using GitBench.Controls.Dialogs;
using GitBench.Git;
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
        var vm = new DeleteLocalBranchDialogViewModel(
            new DeleteLocalBranchRequest(Repo, BranchName, UpstreamRemote, UpstreamBranch),
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>(),
            ctx.Require<ILocalizationService>());

        var s = ctx.Localization().Strings.Value;
        var body = new List<IWidget>
        {
            new Text
            {
                Value = s.BranchesDeleteLocalTitle(BranchName),
                Wrap = TextWrap.Wrap,
                Color = Theme.Color(t => t.DialogBody.BodyText),
            },
            new CheckboxWidget
            {
                Label = s.BranchesDeleteLocalForceLabel,
                Checked = vm.Force,
                Height = Sizes.RowHeight,
            }.WithController<KbmController>(),
            new Text
            {
                Value = s.BranchesDeleteLocalForceHint,
                Wrap = TextWrap.Wrap,
                Color = Theme.Color(t => t.DialogBody.RowTextMissing),
            },
        };
        if (vm.HasUpstream)
        {
            body.Add(new CheckboxWidget
            {
                Label = s.BranchesDeleteLocalRemoteLabel(UpstreamBranch!, UpstreamRemote!),
                Checked = vm.DeleteRemote,
                Height = Sizes.RowHeight,
            }.WithController<KbmController>());
        }

        return new Dialog
        {
            Title = s.BranchesDeleteLocalDialogTitle,
            OnClose = OnClose,
            ViewModel = vm,
            Action = (s.CommonDelete, DialogButtonRole.Destructive),
            Command = vm.Delete,
            ConfirmKeys = true,
            Body = body.ToArray(),
        };
    }
}
