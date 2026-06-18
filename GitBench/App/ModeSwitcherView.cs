using GitBench.Controls;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.App;

internal sealed record ModeSwitcherView : Widget
{
    private const float PillHeight = 28f;
    private const float PillCornerRadius = 5f;

    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<ModeSwitcherViewModel>();
        var theme = ctx.Theme();

        const float innerRadius = PillCornerRadius - 1f;
        return new Box
        {
            Height = PillHeight,
            Children =
            [
                new Box
                {
                    Background = 0x00000000u,
                    BorderSize = BorderSizeStyle.All(1),
                    BorderRadius = BorderRadiusStyle.All(PillCornerRadius),
                    BorderColor = theme.Styles.Bind(s => BorderColorStyle.All(s.ModeSwitcher.PillBorder)),
                    Children =
                    [
                        new Row
                        {
                            CrossAxis = CrossAxisAlignment.Stretch,
                            Children =
                            [
                                new Segment
                                {
                                    Label = "Changes",
                                    Radius = new BorderRadiusStyle { TopLeft = innerRadius, BottomLeft = innerRadius },
                                    Model = vm.LocalChangesSegment,
                                }.WithController<KbmController>(),
                                new Box
                                {
                                    Width = 1f,
                                    Background = theme.Styles.Bind(s => s.ModeSwitcher.SegmentSeparator),
                                },
                                new Segment
                                {
                                    Label = "History",
                                    Radius = new BorderRadiusStyle { TopRight = innerRadius, BottomRight = innerRadius },
                                    Model = vm.HistorySegment,
                                }.WithController<KbmController>(),
                            ],
                        },
                    ],
                },
            ],
        }.BindVm(vm);
    }
}
