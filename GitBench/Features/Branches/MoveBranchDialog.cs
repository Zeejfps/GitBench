using GitBench.Controls.Dialogs;
using GitBench.Features.Commits;
using GitBench.Git;
using GitBench.Messages;
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
            new MoveBranchRequest(Repo, BranchName, Sha),
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>());

        return new Dialog
        {
            Title = "Reset branch to revision",
            OnClose = OnClose,
            ViewModel = vm,
            Width = DialogFrame.WidthWide,
            Action = ("Reset branch", DialogButtonRole.Destructive),
            Command = vm.Move,
            ConfirmKeys = true,
            Body =
            [
                new ThemedText
                {
                    Value = $"Reset '{BranchName}' to the selected revision and check it out",
                    Wrap = TextWrap.Wrap,
                    Color = s => s.DialogBody.BodyText,
                },
                new Clipped
                {
                    Child = new ThemedText
                    {
                        Value = string.IsNullOrEmpty(Summary) ? ShortSha : $"{ShortSha}  {Summary}",
                        Wrap = TextWrap.NoWrap,
                        Color = s => s.DialogBody.BodyText,
                    },
                },
                new ThemedText
                {
                    Value = $"'{BranchName}' has commits that aren't in {ShortSha}. Resetting it here will leave those commits unreachable from any branch.",
                    Wrap = TextWrap.Wrap,
                    Color = s => s.DialogBody.RowTextMissing,
                },
            ],
        };
    }
}
