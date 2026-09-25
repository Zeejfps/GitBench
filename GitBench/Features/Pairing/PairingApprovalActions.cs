using GitBench.Controls;
using GitBench.Features.Assistant;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Pairing;

/// <summary>
/// The answer row of a question in the agent chat: Approve, Allow this file for an edit, and Deny
/// while the question stands, breaking onto a second line in a narrow panel; what was decided once
/// it does not.
/// </summary>
internal sealed record PairingApprovalActions : Widget
{
    public const string AllowFileId = "pairing-allow-file";

    public required PendingToolApproval Pending { get; init; }

    public FileAllowance? Allowance { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var pending = Pending;
        var allowance = Allowance;
        var loc = ctx.Localization();

        IWidget[] allowFile = allowance is null
            ? []
            :
            [
                new ButtonWidget
                {
                    Id = AllowFileId,
                    Style = ButtonStyle.Outline(static s => s.Palette.Accent),
                    Command = allowance.Allow,
                    Children = [new ButtonLabel { Value = L.T(s => s.PairingApprovalAllowFile) }],
                }.WithController<KbmController>(),
            ];

        return new Show
        {
            When = pending.IsPending,
            Then = () => new Wrap
            {
                Gap = Spacing.Sm,
                RunGap = Spacing.Sm,
                Children =
                [
                    new ButtonWidget
                    {
                        Id = ToolApprovalActions.ApproveId,
                        Style = ButtonStyle.Filled(static s => s.Palette.Accent),
                        Command = pending.Approve,
                        Children = [new ButtonLabel { Value = L.T(s => s.AssistantApprovalApprove) }],
                    }.WithController<KbmController>(),
                    .. allowFile,
                    new ButtonWidget
                    {
                        Id = ToolApprovalActions.DenyId,
                        Style = ButtonStyle.Outline(static s => s.Palette.TextSecondary),
                        Command = pending.Deny,
                        Children = [new ButtonLabel { Value = L.T(s => s.AssistantApprovalDeny) }],
                    }.WithController<KbmController>(),
                ],
            },
            Else = () => new Text
            {
                Value = Prop.Bind<string?>(() =>
                {
                    var strings = loc.Strings.Value;
                    if (allowance?.Granted.Value == true) return strings.PairingApprovalApprovedFile;
                    return pending.Outcome.Value switch
                    {
                        ToolApprovalOutcome.Approved => strings.AssistantApprovalApproved,
                        ToolApprovalOutcome.Denied => strings.AssistantApprovalDenied,
                        ToolApprovalOutcome.Cancelled => strings.AssistantApprovalCancelled,
                        ToolApprovalOutcome.Pending => string.Empty,
                        _ => throw new ArgumentOutOfRangeException(nameof(pending), pending.Outcome.Value, "Unknown outcome."),
                    };
                }),
                FontSize = FontSize.Caption,
                Color = Theme.Color(s => s.Palette.TextMuted),
            },
        };
    }
}
