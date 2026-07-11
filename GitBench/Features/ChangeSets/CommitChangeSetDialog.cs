using GitBench.Controls.Dialogs;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Features.ChangeSets;

// Confirms a batch commit across a change set (Phase 5.4). Shows the up-front per-repo staged summary —
// how many files each member will capture — before committing one shared message (plus the Change-Set
// trailer) in every member with staged changes. The commit itself is fire-and-forget through
// ChangeSetOperations, which reports per-repo outcomes with no rollback (Locked decisions #5, #6).
internal sealed record CommitChangeSetDialog : Widget
{
    public required string BranchName { get; init; }
    public required IReadOnlyList<(Guid RepoId, string Name, int Count)> Staged { get; init; }
    public required Action OnConfirm { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var s = ctx.Localization().Strings.Value;

        var body = new List<IWidget>
        {
            new Text
            {
                Value = s.ChangesetsCommitBody(Staged.Count, BranchName),
                Wrap = TextWrap.Wrap,
                Color = Theme.Color(t => t.DialogBody.BodyText),
            },
        };
        foreach (var (_, name, count) in Staged)
            body.Add(new Text
            {
                Value = s.ChangesetsCommitRepoStaged(name, count),
                Wrap = TextWrap.Wrap,
                Color = Theme.Color(t => t.Palette.TextSecondary),
            });

        return new Dialog
        {
            Title = s.ChangesetsCommitTitle,
            OnClose = OnClose,
            Action = (s.ChangesetsCommitConfirm, DialogButtonRole.Primary, () =>
            {
                OnConfirm();
                OnClose();
            }),
            ConfirmKeys = true,
            Body = body.ToArray(),
        };
    }
}
