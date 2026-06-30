using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Review;

/// <summary>
/// The review window's top bar: the range (<c>base → head</c>) on the leading edge, then the current
/// "Increment N of M" position and the "X / Y reviewed" progress on the trailing edge. A placeholder
/// slot for the later range/version comparator can join the trailing group. Reads the pinned
/// <see cref="ReviewWindowViewModel"/> from the build context.
/// </summary>
internal sealed record ReviewHeaderBar : Widget
{
    private const float BarHeight = 38f;

    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<ReviewWindowViewModel>();

        return new Box
        {
            Height = BarHeight,
            Background = Theme.Color(s => s.Palette.Surface),
            BorderSize = new BorderSizeStyle { Bottom = 1 },
            BorderColor = Theme.BorderColor(s => new BorderColorStyle { Bottom = s.Palette.Border }),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = Spacing.Lg, Right = Spacing.Lg },
                    Children =
                    [
                        new Row
                        {
                            Gap = Spacing.Lg,
                            CrossAxis = CrossAxisAlignment.Center,
                            Children =
                            [
                                new Grow
                                {
                                    Child = new Text
                                    {
                                        Value = Prop.Bind(vm.RangeText),
                                        FontSize = FontSize.Body,
                                        Color = Theme.Color(s => s.Palette.TextPrimary),
                                        Overflow = TextOverflow.Ellipsis,
                                        VAlign = TextAlignment.Center,
                                    },
                                },
                                new Text
                                {
                                    Value = Prop.Bind(vm.IncrementLabel),
                                    FontSize = FontSize.Caption,
                                    Color = Theme.Color(s => s.Palette.TextSecondary),
                                    VAlign = TextAlignment.Center,
                                },
                                new Text
                                {
                                    Value = Prop.Bind(vm.ReviewedLabel),
                                    FontSize = FontSize.Caption,
                                    Color = Theme.Color(s => s.Palette.TextSecondary),
                                    VAlign = TextAlignment.Center,
                                },
                            ],
                        },
                    ],
                },
            ],
        };
    }
}
