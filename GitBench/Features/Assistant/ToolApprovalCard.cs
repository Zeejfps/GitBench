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
/// The question a write stops on: which tool, the arguments it would run with, and the two answers.
/// Inline in the transcript, so the pause sits where the conversation is and survives the overlay
/// being closed and opened again.
/// </summary>
internal sealed record ToolApprovalCard : Widget
{
    public required AssistantRow Row { get; init; }

    protected override IWidget Build(Context ctx)
    {
        if (Row.Pending is not { } pending) return Empty.Widget;

        var loc = ctx.Localization();
        var name = pending.ToolName;

        return new Box
        {
            Background = Theme.Color(s => s.Palette.SurfaceRaised),
            BorderSize = BorderSizeStyle.All(1),
            BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.Palette.BorderStrong)),
            BorderRadius = BorderRadiusStyle.All(Radius.Sm),
            Children =
            [
                new Padding
                {
                    Amount = PaddingStyle.All(Spacing.Md),
                    Children =
                    [
                        new Column
                        {
                            Gap = Spacing.Sm,
                            CrossAxis = CrossAxisAlignment.Stretch,
                            Children =
                            [
                                new Row
                                {
                                    Gap = Spacing.Sm,
                                    CrossAxis = CrossAxisAlignment.Center,
                                    Children =
                                    [
                                        new TranscriptGlyph
                                        {
                                            Glyph = LucideIcons.PencilLine,
                                            Tint = Theme.Color(s => s.Status.Warning),
                                        },
                                        new Text
                                        {
                                            Value = Prop.Bind<string?>(
                                                () => loc.Strings.Value.AssistantApprovalTitle(name)),
                                            Weight = FontWeight.Bold,
                                            FontSize = FontSize.Body,
                                            Color = Theme.Color(s => s.Palette.TextPrimary),
                                        },
                                    ],
                                },
                                // Verbatim, not a summary: what is approved is these exact values.
                                new Text
                                {
                                    Value = Prop.Bind<string?>(() => pending.Arguments.Length == 0
                                        ? loc.Strings.Value.AssistantApprovalNoArguments
                                        : pending.Arguments),
                                    Wrap = TextWrap.Wrap,
                                    FontFamily = DiffOptions.MonoFontFamily,
                                    FontSize = FontSize.Caption,
                                    Color = Theme.Color(s => s.Palette.TextSecondary),
                                },
                                new ToolApprovalActions { Pending = pending },
                            ],
                        },
                    ],
                },
            ],
        };
    }
}

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
