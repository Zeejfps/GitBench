using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Branches;

/// <summary>
/// Modal shown when the user clicks Branch in the actions toolbar. Mirrors Fork's
/// "Create Branch" dialog: branch name + starting point (prefilled with the current HEAD's
/// branch name) + a "checkout after create" checkbox. Runs `git branch &lt;name&gt; &lt;start&gt;` or
/// `git checkout -b &lt;name&gt; &lt;start&gt;` depending on the checkbox.
/// </summary>
internal sealed record CreateBranchDialog : Widget
{
    public required Repo Repo { get; init; }
    public required string SuggestedStartPoint { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = new CreateBranchDialogViewModel(
            Repo,
            SuggestedStartPoint,
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>());

        return new Dialog
        {
            Title = "Create branch",
            OnClose = OnClose,
            ViewModel = vm,
            Action = ("Create", DialogButtonRole.Primary),
            Command = vm.Create,
            Body =
            [
                new LabeledInput
                {
                    Label = "Branch name",
                    Value = vm.Name,
                    Status = vm.NameStatus,
                },
                new LabeledInput
                {
                    Label = "Starting point",
                    Value = vm.StartPoint,
                    Hint = "Branch, tag, or commit SHA. Leave blank for HEAD.",
                },
                new Checkbox
                {
                    Label = "Check out after create",
                    Value = vm.Checkout,
                    Height = 22,
                },
            ],
        };
    }
}
