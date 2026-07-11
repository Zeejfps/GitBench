using GitBench.Controls.Dialogs;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.ChangeSets;

// Confirms deleting a change-set branch across every member that carries it (Phase 2.2). The delete
// itself is fire-and-forget through ChangeSetOperations, which reports per-repo outcomes — a member
// whose branch is checked out or not fully merged fails on its own and never blocks the others.
internal sealed record DeleteChangeSetBranchDialog : Widget
{
    public required IReadOnlyList<Guid> RepoIds { get; init; }
    public required string BranchName { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var ops = ctx.Require<ChangeSetOperations>();
        var force = new State<bool>(false);
        var s = ctx.Localization().Strings.Value;

        return new Dialog
        {
            Title = s.ChangesetsDeleteTitle,
            OnClose = OnClose,
            Action = (s.CommonDelete, DialogButtonRole.Destructive, () =>
            {
                ops.DeleteInAll(RepoIds, BranchName, force.Value);
                OnClose();
            }),
            ConfirmKeys = true,
            Body =
            [
                new Text
                {
                    Value = s.ChangesetsDeleteBody(BranchName, RepoIds.Count),
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.BodyText),
                },
                new CheckboxWidget
                {
                    Label = s.ChangesetsDeleteForceLabel,
                    Checked = force,
                    Height = Sizes.RowHeight,
                }.WithController<KbmController>(),
            ],
        };
    }
}
