using GitBench.Controls;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Terminal;

/// <summary>
/// The terminal pane: the mode slot a shell for the active repository will fill. Until the session
/// backend lands it holds a centered placeholder so the mode reads as planned rather than broken.
/// </summary>
internal sealed record TerminalPane : Widget
{
    protected override IWidget Build(Context ctx) => new Box
    {
        Background = Theme.Color(s => s.Palette.Surface),
        Children =
        [
            new Center
            {
                Child = new Column
                {
                    Gap = Spacing.Xl,
                    CrossAxis = CrossAxisAlignment.Center,
                    Children =
                    [
                        new Text
                        {
                            Value = LucideIcons.SquareTerminal,
                            FontFamily = LucideIcons.FontFamily,
                            FontSize = FontSize.Display,
                            HAlign = TextAlignment.Center,
                            VAlign = TextAlignment.Center,
                            Color = Theme.Color(s => s.Palette.TextMuted),
                        },
                        new Column
                        {
                            Gap = Spacing.Md,
                            CrossAxis = CrossAxisAlignment.Center,
                            Children =
                            [
                                new Text
                                {
                                    Value = L.T(s => s.TerminalPlaceholderTitle),
                                    FontSize = FontSize.Heading,
                                    Weight = FontWeight.Bold,
                                    HAlign = TextAlignment.Center,
                                    VAlign = TextAlignment.Center,
                                    Color = Theme.Color(s => s.Palette.TextStrong),
                                },
                                new Text
                                {
                                    Value = L.T(s => s.TerminalPlaceholderHint),
                                    HAlign = TextAlignment.Center,
                                    VAlign = TextAlignment.Center,
                                    Color = Theme.Color(s => s.Palette.TextSecondary),
                                },
                            ],
                        },
                    ],
                },
            },
        ],
    };
}
