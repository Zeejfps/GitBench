using GitBench.Controls;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Widgets;

/// <summary>A top and a bottom pane with a draggable splitter between them. The top can be taken
/// away, leaving the bottom the whole height.</summary>
internal sealed record VerticalSplit : Widget
{
    public required IWidget Top { get; init; }
    public required IWidget Bottom { get; init; }
    public required float BottomFraction { get; init; }
    public IReadable<bool>? TopVisible { get; init; }
    public Action<float>? OnFractionChanged { get; init; }

    protected override View CreateView(Context ctx)
    {
        var splitterHovered = new State<bool>(false);
        var splitter = new RectView();
        splitter.BindThemedBackgroundColor(ctx.Theme(), s =>
            splitterHovered.Value ? s.SidebarSplitter.Hover : s.SidebarSplitter.Idle);

        var split = new VerticalSplitContainer(Top.BuildView(ctx), Bottom.BuildView(ctx), splitter, BottomFraction)
        {
            BottomVisible = true,
            FractionChanged = OnFractionChanged,
        };
        splitter.UseController(ctx.Require<InputSystem>(), () => new SplitterController(
            ctx,
            DragAxis.Y,
            split.AdjustBottomFractionByPixels,
            h => splitterHovered.Value = h));

        if (TopVisible is { } topVisible)
        {
            split.TopVisible = topVisible.Value;
            split.Use(() => topVisible.Subscribe(v => split.TopVisible = v));
        }

        return split;
    }
}
