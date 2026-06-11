using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Controls;

/// <summary>One half of the mode-switcher pill; activates its segment on click.</summary>
internal sealed record SegmentView : Widget
{
    private const float SegmentHeight = 28f;

    public required string Label { get; init; }
    public required BorderRadiusStyle Radius { get; init; }
    public required SegmentViewModel Model { get; init; }

    protected override View CreateView(Context ctx)
    {
        var theme = ctx.Theme();
        var isActive = new State<bool>(false);
        var isHovered = new State<bool>(false);

        var labelView = new TextView(ctx.Canvas)
        {
            Text = Label,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.Center,
        };
        labelView.BindTextColor(() =>
            isActive.Value ? theme.Styles.Value.ModeSwitcher.SegmentActiveText :
            isHovered.Value ? theme.Styles.Value.ModeSwitcher.SegmentHoverText :
            theme.Styles.Value.ModeSwitcher.SegmentIdleText);

        var bg = new RectView
        {
            BorderRadius = Radius,
            Padding = new PaddingStyle { Left = 12, Right = 12 },
            Children = { labelView },
        };
        bg.BindBackgroundColor(() =>
            isActive.Value ? theme.Styles.Value.ModeSwitcher.SegmentActiveBackground :
            isHovered.Value ? theme.Styles.Value.ModeSwitcher.SegmentHoverBackground :
            theme.Styles.Value.ModeSwitcher.SegmentIdleBackground);

        var root = new ContainerView { Height = SegmentHeight };
        root.Children.Add(bg);
        root.Bind(Model.IsActive, b => isActive.Value = b);
        root.UseController(ctx.Require<InputSystem>(),
            () => new HoverableButtonController(Model.Activate, h => isHovered.Value = h));
        return root;
    }
}
