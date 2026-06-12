using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
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

        return new Dialog
        {
            Title = "Deinit submodule",
            OnClose = OnClose,
            ViewModel = vm,
            Action = ("Deinit", DialogButtonRole.Destructive),
            Command = vm.Deinit,
            ConfirmKeys = true,
            Body =
            [
                new ThemedText
                {
                    Value = $"Deinit and remove submodule '{Submodule.DisplayName}'?",
                    Wrap = TextWrap.Wrap,
                    Color = s => s.DialogBody.BodyText,
                },
                new ThemedText
                {
                    Value = "Runs `git submodule deinit` followed by `git rm`. The submodule will " +
                            "be removed from the working tree and the deletion staged in the parent " +
                            "for your next commit.",
                    Wrap = TextWrap.Wrap,
                    Color = s => s.DialogBody.RowTextMissing,
                },
                new Checkbox
                {
                    Label = "Deinit even if dirty",
                    Value = vm.Force,
                    Height = 22,
                },
            ],
        };
    }
}
