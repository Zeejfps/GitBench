using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Widgets;

/// <summary>A "kbd" cap: a small sunken, bordered pill around a key's name.</summary>
internal sealed record KeyCap : Widget
{
    public required string Value { get; init; }

    protected override IWidget Build(Context ctx) => new Box
    {
        Background = Theme.Color(s => s.Palette.SurfaceSunken),
        BorderSize = BorderSizeStyle.All(1),
        BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.Palette.BorderSubtle)),
        BorderRadius = BorderRadiusStyle.All(4f),
        Children =
        [
            new Padding
            {
                Amount = new PaddingStyle { Left = Spacing.Sm, Right = Spacing.Sm, Top = 2, Bottom = 2 },
                Children =
                [
                    new Text
                    {
                        Value = Value,
                        FontSize = FontSize.Caption,
                        Color = Theme.Color(s => s.Palette.TextSecondary),
                        VAlign = TextAlignment.Center,
                        HAlign = TextAlignment.Center,
                    },
                ],
            },
        ],
    };
}
