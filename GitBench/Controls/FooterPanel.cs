using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Controls;

/// <summary>
/// The shared chrome for the Local Changes footer panels — the commit bar, the merge bar, and the
/// operation panel — so they sit on one surface with the same top border and the same padding instead
/// of drifting apart. Stacks the supplied rows in a stretched column; each panel fills in its own rows.
/// </summary>
internal sealed record FooterPanel : Widget
{
    private const int Pad = 10;

    public IWidget[] Children { get; init; } = [];

    protected override IWidget Build(Context ctx) => new Box
    {
        Background = Theme.Color(s => s.CommitBar.Background),
        BorderColor = Theme.BorderColor(s => new BorderColorStyle { Top = s.CommitBar.TopBorder }),
        BorderSize = new BorderSizeStyle { Top = 1 },
        Children =
        [
            new Padding
            {
                Amount = new PaddingStyle { Left = Pad, Right = Pad, Top = Pad, Bottom = Pad },
                Children =
                [
                    new Column
                    {
                        Gap = Spacing.Md,
                        CrossAxis = CrossAxisAlignment.Stretch,
                        Children = Children,
                    },
                ],
            },
        ],
    };
}
