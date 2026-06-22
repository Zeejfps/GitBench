using GitBench.Controls;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Identity;

// The status-bar identity chip — just the look (an author glyph + the active profile name). The owner
// (StatusBarView) bolts on the profile menu with WithMenuController.
internal sealed record IdentityChipButton : Widget<ButtonState>
{
    /// <summary>Author glyph.</summary>
    public required Prop<string?> Icon { get; init; }

    /// <summary>Active profile name.</summary>
    public required Prop<string?> Label { get; init; }

    protected override ButtonState CreateState(Context ctx) => new();

    protected override IWidget Build(Context ctx, ButtonState state) => new Box
    {
        BorderRadius = BorderRadiusStyle.All(4),
        Background = Theme.Color(s => s.StatusBar.IconButtonBackground(state)),
        Children =
        [
            new Padding
            {
                Amount = new PaddingStyle { Left = 4, Right = 4 },
                Children =
                [
                    new Row
                    {
                        Gap = 4,
                        CrossAxis = CrossAxisAlignment.Center,
                        Children =
                        [
                            new Text
                            {
                                FontFamily = LucideIcons.FontFamily,
                                FontSize = 12f,
                                VAlign = TextAlignment.Center,
                                Value = Icon,
                                Color = Theme.Color(s => s.StatusBar.Text),
                            },
                            new Text
                            {
                                FontSize = 11f,
                                VAlign = TextAlignment.Center,
                                Value = Label,
                                Color = Theme.Color(s => s.StatusBar.Text),
                            },
                        ],
                    },
                ],
            },
        ],
    };
}
