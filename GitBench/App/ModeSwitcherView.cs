using GitBench.Controls;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.App;

internal sealed record ModeSwitcherView : Widget
{
    private const float PillHeight = 28f;
    private const float PillCornerRadius = 5f;

    protected override View CreateView(Context ctx)
    {
        var vm = ctx.Require<ModeSwitcherViewModel>();
        var theme = ctx.Theme();

        const float innerRadius = PillCornerRadius - 1f;
        var history = new SegmentView
        {
            Label = "History",
            Radius = new BorderRadiusStyle { TopRight = innerRadius, BottomRight = innerRadius },
            Model = vm.HistorySegment,
        }.BuildView(ctx);
        var localChanges = new SegmentView
        {
            Label = "Changes",
            Radius = new BorderRadiusStyle { TopLeft = innerRadius, BottomLeft = innerRadius },
            Model = vm.LocalChangesSegment,
        }.BuildView(ctx);

        var separator = new RectView { Width = 1f };
        separator.BindBackgroundColor(() => theme.Styles.Value.ModeSwitcher.SegmentSeparator);

        var pill = new RectView
        {
            BackgroundColor = 0x00000000u,
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(PillCornerRadius),
            Children =
            {
                new RowView
                {
                    Children = { localChanges, separator, history },
                },
            },
        };
        pill.BindBorderColor(() => BorderColorStyle.All(theme.Styles.Value.ModeSwitcher.PillBorder));

        var root = new ContainerView { Height = PillHeight };
        root.Children.Add(pill);
        root.UseViewModel(() => vm, _ => { });
        return root;
    }
}
