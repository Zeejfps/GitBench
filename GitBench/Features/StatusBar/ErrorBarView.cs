using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.StatusBar;

/// <summary>
/// Inline warning banner — a bordered box carrying the active theme's banner colors. Shown only
/// while <see cref="Message"/> is non-null; a null message collapses the banner so it leaves no
/// residual gap in the surrounding layout.
/// </summary>
internal sealed record ErrorBarView : Widget
{
    public required IReadable<string?> Message { get; init; }
    public int VerticalPadding { get; init; } = 4;

    protected override IWidget Build(Context ctx) => new Show
    {
        When = new Derived<bool>(() => Message.Value != null),
        Then = () => Banner(Message, VerticalPadding),
    };

    private static IWidget Banner(IReadable<string?> message, int verticalPadding) => new Box
    {
        Background = Theme.Color(s => s.Banner.Background),
        BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.Banner.Border)),
        BorderSize = BorderSizeStyle.All(1),
        BorderRadius = BorderRadiusStyle.All(Radius.Sm),
        Children =
        [
            new Padding
            {
                Amount = new PaddingStyle { Left = Spacing.Md, Right = Spacing.Md, Top = verticalPadding, Bottom = verticalPadding },
                Children =
                [
                    new Text
                    {
                        Value = Prop.Bind(message),
                        VAlign = TextAlignment.Center,
                        Color = Theme.Color(s => s.Banner.Text),
                    },
                ],
            },
        ],
    };
}
