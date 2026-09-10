using GitBench.Controls;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Widgets;

/// <summary>
/// A rounded, bordered search field: a leading search glyph, the growing text input, and an
/// optional trailing control (e.g. a clear button).
/// </summary>
internal sealed record SearchInputBox : Widget
{
    public required IWidget Input { get; init; }
    public IWidget? Trailing { get; init; }

    protected override IWidget Build(Context ctx)
    {
        IWidget[] rowChildren = Trailing is { } trailing
            ? [SearchGlyph(), new Grow { Child = Input }, trailing]
            : [SearchGlyph(), new Grow { Child = Input }];

        return new Box
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(Radius.Sm),
            Background = Theme.Color(s => s.TextInput.Background),
            BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.TextInput.Border)),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = Spacing.Md, Right = Spacing.Sm, Top = Spacing.Hair, Bottom = Spacing.Hair },
                    Children =
                    [
                        new Row
                        {
                            CrossAxis = CrossAxisAlignment.Center,
                            Gap = Spacing.Md,
                            Children = rowChildren,
                        },
                    ],
                },
            ],
        };
    }

    private static IWidget SearchGlyph() => new Text
    {
        FontFamily = LucideIcons.FontFamily,
        FontSize = FontSize.Default,
        Value = LucideIcons.Search,
        HAlign = TextAlignment.Center,
        VAlign = TextAlignment.Center,
        Color = Theme.Color(s => s.CommitsView.RowTextDim),
    };
}
