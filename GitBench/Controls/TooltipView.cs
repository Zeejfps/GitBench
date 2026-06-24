using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Controls;

public sealed record TooltipView : Widget
{
    private const int HorizontalPadding = 8;
    private const int VerticalPadding = 4;

    public required string Text { get; init; }

    protected override IWidget Build(Context ctx) => new Box
    {
        Background = Theme.Color(s => s.Tooltip.Background),
        Children =
        [
            new Padding
            {
                Amount = new PaddingStyle
                {
                    Left = HorizontalPadding,
                    Right = HorizontalPadding,
                    Top = VerticalPadding,
                    Bottom = VerticalPadding,
                },
                Children =
                [
                    new Text
                    {
                        Value = Text,
                        FontSize = FontSize.Body,
                        VAlign = TextAlignment.Center,
                        Color = Theme.Color(s => s.Tooltip.Text),
                    },
                ],
            },
        ],
    };
}
