using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Infrastructure;
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
        var primary = Primary;
        var submodulePath = SubmodulePaths.Relative(Primary.Path, Submodule.Path);
        var onClose = OnClose;
        var gitService = ctx.Require<IGitSubmoduleOperations>();
        var bus = ctx.Require<IMessageBus>();

        var force = new State<bool>(false);
        var deinit = AsyncCommand.ForOutcome(
            ctx.Require<IUiDispatcher>(),
            work: () => gitService.DeinitSubmodule(primary, submodulePath, force.Value),
            onSuccess: () =>
            {
                bus.Broadcast(new SubmodulesChangedMessage(primary.Id));
                bus.Broadcast(new WorkingTreeChangedMessage(primary.Id));
                onClose();
            });

        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            Title = s.SubmodulesDeinitTitle,
            OnClose = onClose,
            Action = (s.SubmodulesDeinitAction, DialogButtonRole.Destructive),
            Command = deinit,
            ConfirmKeys = true,
            Body =
            [
                new DialogBodyText { Value = s.SubmodulesDeinitConfirm(Submodule.DisplayName) },
                new Text
                {
                    Value = s.SubmodulesDeinitDesc,
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.RowTextMissing),
                },
                new CheckboxWidget
                {
                    Label = s.SubmodulesDeinitForceLabel,
                    Checked = force,
                    Height = Sizes.RowHeight,
                }.WithController<KbmController>(),
            ],
        };
    }
}
