using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Stash;

/// <summary>
/// Confirmation modal shown when the user picks "Delete…" on a stash row. Running
/// `git stash drop` is destructive — the stash cannot be recovered from the UI
/// afterwards, so the action is gated behind this prompt.
/// </summary>
internal sealed record DeleteStashDialog : Widget
{
    public required Repo Repo { get; init; }
    public required int Index { get; init; }
    public required string Subject { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = new DropStashViewModel(
            Repo,
            Index,
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>());

        return new Dialog
        {
            Title = "Delete stash?",
            OnClose = OnClose,
            ViewModel = vm,
            Width = DialogFrame.WidthCompact,
            Action = ("Delete", DialogButtonRole.Destructive),
            Command = vm.Drop,
            ConfirmKeys = true,
            Body =
            [
                new Text
                {
                    Value = $"{Subject}\n\nThis stash will be permanently deleted. This cannot be undone.",
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(s => s.DialogBody.BodyText),
                },
            ],
        };
    }
}
