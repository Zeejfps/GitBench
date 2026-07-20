using GitBench.Controls.Dialogs;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Repos;

/// <summary>
/// Confirmation modal shown when the user picks "Delete group" on a RepoBar group header.
/// Deleting a group doesn't remove its repositories — they move to an adjacent group — but
/// the grouping itself is lost, so the action is gated behind this prompt.
/// </summary>
internal sealed record DeleteGroupDialog : Widget
{
    public required Group Group { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var registry = ctx.Require<IRepoRegistry>();
        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            Title = s.ReposGroupDeleteTitle,
            OnClose = OnClose,
            Width = DialogFrame.WidthCompact,
            Action = (s.CommonDelete, DialogButtonRole.Destructive, () =>
            {
                registry.DeleteGroup(Group.Id);
                OnClose();
            }),
            ConfirmKeys = true,
            Body =
            [
                new Text
                {
                    Value = s.ReposGroupDeleteBody(Group.Name.Value),
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.BodyText),
                },
            ],
        };
    }
}
