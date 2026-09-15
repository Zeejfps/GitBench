using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
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
        var repo = Repo;
        var index = Index;
        var onClose = OnClose;
        var gitService = ctx.Require<IGitStashOperations>();
        var bus = ctx.Require<IMessageBus>();

        var drop = AsyncCommand.ForOutcome(
            ctx.Require<IUiDispatcher>(),
            work: () => gitService.DropStash(repo, index),
            onSuccess: () =>
            {
                bus.Broadcast(new RefsChangedMessage(repo.Id));
                onClose();
            });

        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            Title = s.StashDeleteTitle,
            OnClose = onClose,
            Width = DialogFrame.WidthCompact,
            Action = (s.CommonDelete, DialogButtonRole.Destructive),
            Command = drop,
            ConfirmKeys = true,
            Body =
            [
                new Text
                {
                    Value = s.StashDeleteBody(Subject),
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.BodyText),
                },
            ],
        };
    }
}
