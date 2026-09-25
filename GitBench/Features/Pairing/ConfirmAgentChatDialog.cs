using GitBench.Controls.Dialogs;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Pairing;

/// <summary>Asked before the agent chat is closed or restarted: either way the agent is stopped and
/// what was said is gone.</summary>
internal sealed record ConfirmAgentChatDialog : Widget
{
    public required string Title { get; init; }

    public required string Body { get; init; }

    public required string ActionLabel { get; init; }

    public required Action OnClose { get; init; }

    public required Action OnConfirm { get; init; }

    protected override IWidget Build(Context ctx) => new Dialog
    {
        Title = Title,
        OnClose = OnClose,
        Width = DialogFrame.WidthCompact,
        CancelLabel = ctx.Localization().Strings.Value.PairingKeepTalking,
        Action = (ActionLabel, DialogButtonRole.Destructive, () =>
        {
            OnClose();
            OnConfirm();
        }),
        ConfirmKeys = true,
        Body = [new DialogBodyText { Value = Body }],
    };
}
