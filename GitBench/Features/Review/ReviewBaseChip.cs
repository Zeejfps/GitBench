using GitBench.Controls;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Review;

// The header's base side, rendered as a clickable chip (the base ref name + a ChevronDown) so the
// comparison base reads as interactive — the affordance the old flat range text lacked. Just the
// look; the owner (ReviewHeaderBar) bolts on the provenance tooltip and the base dropdown via
// WithTooltip / WithMenuController.
internal sealed record ReviewBaseChip : Widget<ButtonState>
{
    /// <summary>The resolved base ref name (or "Resolving base…" while the first stack loads).</summary>
    public required Prop<string?> Label { get; init; }

    protected override ButtonState CreateState(Context ctx) => new();

    protected override IWidget Build(Context ctx, ButtonState state) => new Box
    {
        BorderRadius = BorderRadiusStyle.All(Radius.Sm),
        // Transparent idle, a subtle hover wash — the shared row-hover token, auto-tracked off the
        // button state so it lights up on hover.
        Background = Theme.Color(s => state.Enabled.Value && state.Hovered.Value ? s.RowSelection.FillHover : 0u),
        Children =
        [
            new Padding
            {
                Amount = new PaddingStyle { Left = Spacing.Sm, Right = Spacing.Sm, Top = Spacing.Hair, Bottom = Spacing.Hair },
                Children =
                [
                    new Row
                    {
                        Gap = Spacing.Xs,
                        CrossAxis = CrossAxisAlignment.Center,
                        Children =
                        [
                            new Text
                            {
                                Value = Label,
                                FontSize = FontSize.Body,
                                VAlign = TextAlignment.Center,
                                Color = Theme.Color(s => s.Palette.TextPrimary),
                                Overflow = TextOverflow.Ellipsis,
                            },
                            new Text
                            {
                                FontFamily = LucideIcons.FontFamily,
                                FontSize = FontSize.Caption,
                                VAlign = TextAlignment.Center,
                                Value = LucideIcons.ChevronDown,
                                Color = Theme.Color(s => s.Palette.TextSecondary),
                            },
                        ],
                    },
                ],
            },
        ],
    };
}
