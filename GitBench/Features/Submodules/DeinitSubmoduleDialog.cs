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
/// Confirmation modal for `git submodule deinit` + `git rm`. Refuses if the submodule
/// has uncommitted changes unless Force is checked (delegates the safety check to git).
/// </summary>
internal sealed record DeinitSubmoduleDialog : Widget
{
    public required Repo Primary { get; init; }
    public required Repo Submodule { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = new DeinitSubmoduleDialogViewModel(
            new DeinitSubmoduleViewRequest(Primary, Submodule),
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>());

        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            Title = s.SubmodulesDeinitTitle,
            OnClose = OnClose,
            ViewModel = vm,
            Action = ("Deinit", DialogButtonRole.Destructive),
            Command = vm.Deinit,
            ConfirmKeys = true,
            Body =
            [
                new Text
                {
                    Value = s.SubmodulesDeinitConfirm(Submodule.DisplayName),
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.BodyText),
                },
                new Text
                {
                    Value = s.SubmodulesDeinitDesc,
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.RowTextMissing),
                },
                new CheckboxWidget
                {
                    Label = s.SubmodulesDeinitForceLabel,
                    Checked = vm.Force,
                    Height = 22,
                }.WithController<KbmController>(),
            ],
        };
    }
}
