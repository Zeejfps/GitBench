using GitBench.Controls.Dialogs;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
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
        var repo = Repo;
        var oldName = CurrentName;
        var onClose = OnClose;
        var gitService = ctx.Require<IGitBranchOperations>();
        var bus = ctx.Require<IMessageBus>();
        var status = ctx.Require<IRepoStatusStore>();
        var head = ctx.Require<IRepoHeadStore>();
        var loc = ctx.Localization();

        var name = new State<string>(oldName);
        var force = new State<bool>(false);
        var nameStatus = new Derived<FieldStatus?>(() =>
        {
            var strings = loc.Strings.Value;
            return RefNameRules.Validate(name.Value, strings, strings.RefnameNounBranch);
        });
        var gate = new Derived<bool>(() =>
            name.Value.Length > 0 && name.Value != oldName && RefNameRules.IsValid(name.Value));

        var rename = AsyncCommand.ForOutcome(
            ctx.Require<IUiDispatcher>(),
            work: () => gitService.RenameBranch(repo, oldName, name.Value, force.Value),
            onSuccess: () =>
            {
                bus.Broadcast(new RefsChangedMessage(repo.Id));
                onClose();
            },
            gate: gate,
            // Renaming the checked-out branch doesn't move HEAD to a different commit, but it does
            // change what HEAD is called — which is the thing every "current branch" reader holds.
            // Renaming any other branch leaves HEAD alone and declares nothing.
            onStart: () => status.Active.Value.EffectiveBranchName == oldName
                ? head.BeginMove(repo, name.Value)
                : null);

        var s = loc.Strings.Value;
        return new Dialog
        {
            Title = s.BranchesRenameTitle,
            OnClose = onClose,
            Action = (s.CommonRename, DialogButtonRole.Primary),
            Command = rename,
            Body =
            [
                new Text
                {
                    Value = s.BranchesRenameDescription(CurrentName),
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.BodyText),
                },
                new LabeledInput
                {
                    Label = s.BranchesRenameNewNameLabel,
                    Value = name,
                    Status = nameStatus,
                    SelectAllOnOpen = true,
                },
                new CheckboxWidget
                {
                    Label = s.BranchesRenameForceLabel,
                    Checked = force,
                    Height = Sizes.RowHeight,
                }.WithController<KbmController>(),
            ],
        };
    }
}
