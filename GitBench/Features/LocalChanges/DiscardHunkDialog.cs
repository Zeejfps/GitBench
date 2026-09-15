using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.LocalChanges;

internal sealed record DiscardHunkDialog : Widget
{
    public required Repo Repo { get; init; }
    public required string Path { get; init; }
    public required string Patch { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var repo = Repo;
        var patch = Patch;
        var onClose = OnClose;
        var gitService = ctx.Require<IGitWorkingTreeOperations>();
        var bus = ctx.Require<IMessageBus>();

        var discard = AsyncCommand.ForOutcome(
            ctx.Require<IUiDispatcher>(),
            work: () => gitService.ApplyPatch(repo, patch, cached: false, reverse: true),
            onSuccess: () =>
            {
                bus.Broadcast(new WorkingTreeChangedMessage(repo.Id));
                onClose();
            });

        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            Title = s.LocalchangesDiscardHunkTitle,
            OnClose = onClose,
            Width = DialogFrame.WidthCompact,
            Height = 200f,
            Action = (s.CommonDiscard, DialogButtonRole.Destructive),
            Command = discard,
            ConfirmKeys = true,
            Body =
            [
                new Grow
                {
                    Child = new Text
                    {
                        Value = s.LocalchangesDiscardHunkBody(Path),
                        Wrap = TextWrap.Wrap,
                        Color = Theme.Color(t => t.DialogBody.BodyText),
                    },
                },
            ],
        };
    }
}

