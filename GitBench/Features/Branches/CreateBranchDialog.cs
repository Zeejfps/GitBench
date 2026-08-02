using GitBench.Controls.Dialogs;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
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

    /// What the branch is created from. <see cref="GitRef.Head"/> means "wherever HEAD is when
    /// Create runs", resolved by git under the repo lock — pass it rather than the current branch's
    /// name, so a name read while HEAD was moving can't become the starting point.
    public required GitRef StartPoint { get; init; }

    /// What the starting-point field shows for <see cref="StartPoint"/> — a branch name reads better
    /// than "HEAD". Purely a label: editing it is what replaces the ref.
    public required string StartPointLabel { get; init; }

    /// Pre-fills the branch-name field (e.g. "feature/admin/" to create inside a folder).
    /// Empty for a plain create. Editable — the user can change or clear it.
    public string InitialName { get; init; } = "";

    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = new CreateBranchDialogViewModel(
            Repo,
            StartPoint,
            StartPointLabel,
            InitialName,
            ctx.Require<IGitBranchOperations>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>(),
            ctx.Require<IRepoHeadStore>(),
            ctx.Require<ILocalizationService>());

        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            Title = s.BranchesCreateTitle,
            OnClose = OnClose,
            ViewModel = vm,
            Action = (s.CommonCreate, DialogButtonRole.Primary),
            Command = vm.Create,
            Body =
            [
                new LabeledInput
                {
                    Label = s.BranchesCreateNameLabel,
                    Value = vm.Name,
                    Status = vm.NameStatus,
                },
                new LabeledInput
                {
                    Label = s.BranchesCreateStartPointLabel,
                    Value = vm.StartPoint,
                    Hint = s.BranchesCreateStartPointHint,
                },
                new CheckboxWidget
                {
                    Label = s.BranchesCreateCheckoutLabel,
                    Checked = vm.Checkout,
                    Height = Sizes.RowHeight,
                }.WithController<KbmController>(),
            ],
        };
    }
}
