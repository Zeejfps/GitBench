using GitBench.Features.Notifications;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.StatusBar;

/// <summary>
/// Thin status bar spanning the full width of the window bottom (the outer border layout's South
/// region). Carries the bar's readouts and controls with the toast slot layered over them, so a
/// toast lands here rather than floating over the workspace and the commit button.
/// </summary>
internal sealed record StatusBarView : Widget
{
    private const int BarHeight = Sizes.RowHeight;
    private const int HorizontalPadding = 8;

    // Fix the bar height on the outer box, not the inner one: the inner box also carries a 1px
    // top border, so giving it an explicit Height would make its measured size exceed its
    // laid-out size by the border and leave a 1px gap above the bar. Sizing the outer box and
    // letting the inner one fill the region keeps it flush against the content.
    protected override IWidget Build(Context ctx) => new Box
    {
        Height = BarHeight,
        Children =
        [
            new Box
            {
                BorderSize = new BorderSizeStyle { Top = 1 },
                Background = Theme.Color(s => s.StatusBar.Background),
                BorderColor = Theme.BorderColor(s => new BorderColorStyle { Top = s.StatusBar.TopBorder }),
                Children =
                [
                    new Padding
                    {
                        Amount = new PaddingStyle { Left = HorizontalPadding, Right = HorizontalPadding },
                        Children =
                        [
                            new Stack { Children = [new StatusBarContentView(), new ToastSlotView()] },
                        ],
                    },
                ],
            },
        ],
    };
}
