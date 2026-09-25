using GitBench.Controls;
using GitBench.Features.Assistant;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Pairing;

/// <summary>
/// The answer row of an edit: Allow this file beside Deny / Approve while the question stands, and
/// what was decided once it does not.
/// </summary>
internal sealed record FileAllowanceActions : Widget
{
    public const string AllowFileId = "pairing-allow-file";

    public required PendingToolApproval Pending { get; init; }

    public required FileAllowance Allowance { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var allowance = Allowance;
        var loc = ctx.Localization();
        return new Switch<bool>
        {
            Value = allowance.Granted,
            Case = granted => granted
                ? new Text
                {
                    Value = Prop.Bind<string?>(() => loc.Strings.Value.PairingApprovalApprovedFile),
                    FontSize = FontSize.Caption,
                    HAlign = TextAlignment.End,
                    Color = Theme.Color(s => s.Palette.TextMuted),
                }
                : new Row
                {
                    Gap = Spacing.Sm,
                    MainAxis = MainAxisAlignment.End,
                    Children =
                    [
                        new Show
                        {
                            When = Pending.IsPending,
                            Then = () => new ButtonWidget
                            {
                                Id = AllowFileId,
                                Style = ButtonStyle.Outline(static s => s.Palette.Accent),
                                Command = allowance.Allow,
                                Children = [new ButtonLabel { Value = L.T(s => s.PairingApprovalAllowFile) }],
                            }.WithController<KbmController>(),
                        },
                        new ToolApprovalActions { Pending = Pending },
                    ],
                },
        };
    }
}
