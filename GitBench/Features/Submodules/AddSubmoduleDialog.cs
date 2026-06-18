using GitBench.Controls.Dialogs;
using GitBench.Git;
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
            ctx.Require<IMessageBus>());

        return new Dialog
        {
            Title = "Add submodule",
            OnClose = OnClose,
            ViewModel = vm,
            Action = ("Add", DialogButtonRole.Primary),
            Command = vm.Add,
            Body =
            [
                new LabeledInput
                {
                    Label = "Repository URL",
                    Value = vm.Url,
                },
                new LabeledInput
                {
                    Label = "Path inside parent",
                    Value = vm.Path,
                    Hint = "Where to clone the submodule, relative to the parent root.",
                },
                new LabeledInput
                {
                    Label = "Track branch (optional)",
                    Value = vm.Branch,
                    Hint = "Leave blank to pin to the upstream HEAD at clone time.",
                    Status = vm.BranchStatus,
                },
                new CheckboxWidget
                {
                    Label = "Force (allow paths previously used)",
                    Checked = vm.Force,
                    Height = 22,
                }.WithController<KbmController>(),
            ],
        };
    }
}
