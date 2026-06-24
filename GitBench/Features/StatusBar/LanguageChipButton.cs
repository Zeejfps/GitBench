using GitBench.Controls;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.StatusBar;

// The status-bar language chip — just the look (the active locale's short code). The owner
// (StatusBarView) bolts on the language menu with WithMenuController. This is the cross-platform
// way to switch language; the native macOS Language menu carries the same items but exists only
// on macOS.
internal sealed record LanguageChipButton : Widget<ButtonState>
{
    /// <summary>Active locale's short code (e.g. "EN", "AR").</summary>
    public required Prop<string?> Label { get; init; }

    protected override ButtonState CreateState(Context ctx) => new();

    protected override IWidget Build(Context ctx, ButtonState state) => new Box
    {
        BorderRadius = BorderRadiusStyle.All(Radius.Sm),
        Background = Theme.Color(s => s.StatusBar.IconButtonBackground(state)),
        Children =
        [
            new Padding
            {
                Amount = new PaddingStyle { Left = Spacing.Sm, Right = Spacing.Sm },
                Children =
                [
                    new Text
                    {
                        FontSize = FontSize.Caption,
                        VAlign = TextAlignment.Center,
                        Value = Label,
                        Color = Theme.Color(s => s.StatusBar.Text),
                    },
                ],
            },
        ],
    };
}
