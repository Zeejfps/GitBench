using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Submodules;

/// <summary>
/// Modal shown from a primary RepoRow's "Add submodule…" menu. Collects the URL, path,
/// and optional tracked branch that `git submodule add` needs, plus a force toggle
/// for re-using a path that's been previously used.
/// </summary>
internal sealed record AddSubmoduleDialog : Widget
{
    public required Repo Primary { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = new AddSubmoduleDialogViewModel(
            new AddSubmoduleViewRequest(Primary),
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>(),
            ctx.Require<ILocalizationService>());

        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            Title = s.SubmodulesAddTitle,
            OnClose = OnClose,
            ViewModel = vm,
            Action = (s.CommonAdd, DialogButtonRole.Primary),
            Command = vm.Add,
            Body =
            [
                new LabeledInput
                {
                    Label = s.CommonRepositoryUrl,
                    Value = vm.Url,
                },
                new LabeledInput
                {
                    Label = s.SubmodulesAddPathLabel,
                    Value = vm.Path,
                    Hint = s.SubmodulesAddPathHint,
                },
                new LabeledInput
                {
                    Label = s.SubmodulesAddBranchLabel,
                    Value = vm.Branch,
                    Hint = s.SubmodulesAddBranchHint,
                    Status = vm.BranchStatus,
                },
                new CheckboxWidget
                {
                    Label = s.SubmodulesAddForceLabel,
                    Checked = vm.Force,
                    Height = Sizes.RowHeight,
                }.WithController<KbmController>(),
            ],
        };
    }
}
