using GitBench.Controls;
using GitBench.Features.Diff;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Assistant;

/// <summary>
/// The answer row: the two buttons while the question stands, and what was decided once it does not.
/// </summary>
internal sealed record ToolApprovalActions : Widget
{
    public const string ApproveId = "assistant-approve";
    public const string DenyId = "assistant-deny";

    public required PendingToolApproval Pending { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var pending = Pending;
        var loc = ctx.Localization();

        return new Switch<ToolApprovalOutcome>
        {
            Value = pending.Outcome,
            Case = outcome => outcome == ToolApprovalOutcome.Pending
                ? new Row
                {
                    Gap = Spacing.Sm,
                    MainAxis = MainAxisAlignment.End,
                    Children =
                    [
                        new ButtonWidget
                        {
                            Id = DenyId,
                            Style = ButtonStyle.Outline(static s => s.Palette.TextSecondary),
                            Command = pending.Deny,
                            Children = [new ButtonLabel { Value = L.T(s => s.AssistantApprovalDeny) }],
                        }.WithController<KbmController>(),
                        new ButtonWidget
                        {
                            Id = ApproveId,
                            Style = ButtonStyle.Filled(static s => s.Palette.Accent),
                            Command = pending.Approve,
                            Children = [new ButtonLabel { Value = L.T(s => s.AssistantApprovalApprove) }],
                        }.WithController<KbmController>(),
                    ],
                }
                : new Text
                {
                    Value = Prop.Bind<string?>(() =>
                    {
                        var strings = loc.Strings.Value;
                        return outcome switch
                        {
                            ToolApprovalOutcome.Approved => strings.AssistantApprovalApproved,
                            ToolApprovalOutcome.Denied => strings.AssistantApprovalDenied,
                            _ => strings.AssistantApprovalCancelled,
                        };
                    }),
                    FontSize = FontSize.Caption,
                    HAlign = TextAlignment.End,
                    Color = Theme.Color(s => s.Palette.TextMuted),
                },
        };
    }
}
