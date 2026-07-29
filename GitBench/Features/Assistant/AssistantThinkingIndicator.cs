using GitBench.Controls;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Assistant;

/// <summary>
/// The placeholder that stands in for an answer while the model is still reasoning: a breathing
/// skeleton line under a "Thinking…" label. Thinking content is never shown, only that it is
/// happening.
/// </summary>
/// <remarks>
/// Mount it fresh (behind a <see cref="Show"/>) rather than caching one — the pulse stops on unmount
/// and a reattached instance would never breathe again.
/// </remarks>
internal sealed record AssistantThinkingIndicator : Widget
{
    private const float BarHeight = 8f;
    private static readonly float[] BarWidths = [220f, 168f];

    protected override IWidget Build(Context ctx)
    {
        var theme = ctx.Theme();
        var pulse = new Pulse(ctx.Require<IFrameTicker>());
        pulse.Start();

        var bars = new IWidget[BarWidths.Length];
        for (var i = 0; i < BarWidths.Length; i++)
        {
            var dim = i == 0 ? 1f : 0.7f;
            bars[i] = new Box
            {
                Width = BarWidths[i],
                Height = BarHeight,
                Background = Prop.Bind(() =>
                    SkeletonPainter.Fill(theme.Styles.Value.Palette.TextPrimary, pulse.Value.Value, dim)),
                BorderRadius = BorderRadiusStyle.All(BarHeight / 2f),
            };
        }

        return new Column
        {
            Gap = Spacing.Sm,
            Children =
            [
                new Text
                {
                    Value = L.T(s => s.AssistantThinking),
                    FontSize = FontSize.Caption,
                    Color = Theme.Color(s => s.Palette.TextMuted),
                },
                new Column { Gap = Spacing.Sm, Children = bars },
            ],
        }.Use(_ => pulse);
    }
}
