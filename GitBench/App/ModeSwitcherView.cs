using GitBench.Controls;
using GitBench.Localization;
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
        var s = ctx.Localization().Strings.Value;

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
                    BorderColor = theme.Styles.Bind(t => BorderColorStyle.All(t.ModeSwitcher.PillBorder)),
                    Children =
                    [
                        new Row
                        {
                            CrossAxis = CrossAxisAlignment.Stretch,
                            Children =
                            [
                                new Segment
                                {
                                    Label = s.AppModeChanges,
                                    Radius = new BorderRadiusStyle { TopLeft = innerRadius, BottomLeft = innerRadius },
                                    Model = vm.LocalChangesSegment,
                                }.WithController<KbmController>(),
                                new Box
                                {
                                    Width = 1f,
                                    Background = theme.Styles.Bind(t => t.ModeSwitcher.SegmentSeparator),
                                },
                                new Segment
                                {
                                    Label = s.AppModeHistory,
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
