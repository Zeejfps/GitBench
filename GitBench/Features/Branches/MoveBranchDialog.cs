using GitBench.Controls.Dialogs;
using GitBench.Features.Commits;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Branches;

/// <summary>
/// Confirmation modal for "Reset &lt;branch&gt; to here" when the move is NOT a fast-forward —
/// the branch has commits that aren't in the selected revision, so force-moving it would
/// leave those commits unreachable. The fast-forward case skips this dialog (applied directly
/// by <see cref="CommitsViewModel"/>). Runs `git checkout -B &lt;branch&gt; &lt;sha&gt;` on confirm.
/// </summary>
internal sealed record MoveBranchDialog : Widget
{
    public required Repo Repo { get; init; }
    public required string BranchName { get; init; }
    public required string Sha { get; init; }
    public required string ShortSha { get; init; }
    public required string Summary { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = new MoveBranchDialogViewModel(
            Repo,
            BranchName,
            Sha,
            ctx.Require<IGitBranchOperations>(),
            ctx.Require<IRepoHeadStore>(),
            ctx.Localization(),
            OnClose);

        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            Title = s.BranchesMoveTitle,
            OnClose = OnClose,
            Width = DialogFrame.WidthWide,
            Action = (s.BranchesMoveAction, DialogButtonRole.Destructive, vm.Move),
            ConfirmKeys = true,
            Body =
            [
                new DialogBodyText { Value = s.BranchesMoveDescription(BranchName) },
                new Clipped
                {
                    Child = new Text
                    {
                        Value = string.IsNullOrEmpty(Summary) ? ShortSha : $"{ShortSha}  {Summary}",
                        Wrap = TextWrap.NoWrap,
                        Color = Theme.Color(t => t.DialogBody.BodyText),
                    },
                },
                new Text
                {
                    Value = s.BranchesMoveWarning(BranchName, ShortSha),
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.RowTextMissing),
                },
            ],
        };
    }
}
