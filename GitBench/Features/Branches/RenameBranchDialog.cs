using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Branches;

/// <summary>
/// Modal shown when the user picks "Rename…" on a local branch row. Full branch path is
/// editable (slashes allowed) so cross-folder moves like feature/login → bugs/login work
/// the same as in `git branch -m`. The force checkbox switches the underlying call to -M,
/// allowing the rename to overwrite an existing branch of the new name.
/// </summary>
internal sealed record RenameBranchDialog : Widget
{
    public required Repo Repo { get; init; }
    public required string CurrentName { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = new RenameBranchDialogViewModel(
            new RenameBranchRequest(Repo, CurrentName),
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>());

        return new Dialog
        {
            Title = "Rename branch",
            OnClose = OnClose,
            ViewModel = vm,
            Action = ("Rename", DialogButtonRole.Primary),
            Command = vm.Rename,
            Body =
            [
                new Text
                {
                    Value = $"Renaming '{CurrentName}'",
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(s => s.DialogBody.BodyText),
                },
                new LabeledInput
                {
                    Label = "New name",
                    Value = vm.Name,
                    Status = vm.NameStatus,
                    SelectAllOnOpen = true,
                },
                new CheckboxWidget
                {
                    Label = "Force rename even if target exists",
                    Checked = vm.Force,
                    Height = 22,
                }.WithController<KbmController>(),
            ],
        };
    }
}
