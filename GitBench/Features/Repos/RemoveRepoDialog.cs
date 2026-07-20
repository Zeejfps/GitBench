using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Repos;

/// <summary>
/// Confirmation modal shown when the user picks "Remove repo" on a RepoBar row. Removal
/// only drops the repository from GitBench's sidebar — the files on disk are untouched —
/// but it discards worktree/submodule rows and any hotkey, so it's gated behind this prompt.
/// </summary>
internal sealed record RemoveRepoDialog : Widget
{
    public required Repo Repo { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var registry = ctx.Require<IRepoRegistry>();
        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            Title = s.ReposRepoRemoveTitle,
            OnClose = OnClose,
            Width = DialogFrame.WidthCompact,
            Action = (s.CommonRemove, DialogButtonRole.Destructive, () =>
            {
                registry.RemoveRepo(Repo.Id);
                OnClose();
            }),
            ConfirmKeys = true,
            Body =
            [
                new Text
                {
                    Value = s.ReposRepoRemoveBody(Repo.DisplayName),
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.BodyText),
                },
            ],
        };
    }
}
