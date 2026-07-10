using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitBench.Controls;

/// <summary>
/// Wraps a sidebar content view and lays a draggable splitter strip on its inline-end edge (its right
/// edge under LTR, its left edge under RTL — the side facing the main content). The wrapper itself owns
/// the sidebar's width via <see cref="View.Width"/>, so the surrounding <c>BorderLayoutView</c> reads
/// the new width on each layout pass after a drag. Width is clamped to <see cref="_minWidth"/> /
/// <see cref="_maxWidth"/>.
/// </summary>
internal sealed class ResizableLeftSidebar : ContainerView
{
    private const float SplitterThickness = 5f;

    private readonly View _content;
    private readonly View _splitter;
    private readonly float _minWidth;
    private readonly float _maxWidth;

    public Action<float>? WidthChanged { get; set; }

    public ResizableLeftSidebar(View content, View splitter, float initialWidth, float minWidth, float maxWidth)
    {
        _content = content;
        _splitter = splitter;
        _minWidth = minWidth;
        _maxWidth = maxWidth;
        Width = Math.Clamp(initialWidth, _minWidth, _maxWidth);
        AddChildToSelf(_content);
        AddChildToSelf(_splitter);
    }

    // Dragging the splitter toward the main content grows the sidebar. Under LTR the splitter is on the
    // right, so a rightward move (positive dx) grows it; under RTL it's on the left, so the sense flips.
    // Clamping keeps the sidebar usable at both extremes (it can't disappear or eat the main view).
    public void AdjustWidthByPixels(float dx)
    {
        var signed = IsRtl ? -dx : dx;
        var newWidth = Math.Clamp((float)Width + signed, _minWidth, _maxWidth);
        if (Math.Abs(newWidth - (float)Width) < 0.5f) return;
        Width = newWidth;
        WidthChanged?.Invoke(newWidth);
    }

    protected override void OnLayoutChildren()
    {
        var pos = Position;
        if (pos.Width <= 0f || pos.Height <= 0f) return;

        var contentWidth = Math.Max(0f, pos.Width - SplitterThickness);
        // The splitter takes the inline-end edge (the side facing the main content): the right edge under
        // LTR, the left edge under RTL, with the content shifted over to make room.
        var rtl = IsRtl;
        var contentLeft = rtl ? pos.Left + SplitterThickness : pos.Left;
        var splitterLeft = rtl ? pos.Left : pos.Left + contentWidth;

        _content.LeftConstraint = contentLeft;
        _content.BottomConstraint = pos.Bottom;
        _content.WidthConstraint = contentWidth;
        _content.HeightConstraint = pos.Height;
        _content.LayoutSelf();

        _splitter.LeftConstraint = splitterLeft;
        _splitter.BottomConstraint = pos.Bottom;
        _splitter.WidthConstraint = SplitterThickness;
        _splitter.HeightConstraint = pos.Height;
        _splitter.LayoutSelf();
    }

    /// <summary>
    /// Convenience factory: builds the sidebar wrapper, the splitter rect (with hover
    /// styling), and wires the splitter controller in one place. Returns the wrapper.
    /// </summary>
    public static ResizableLeftSidebar Build(
        Context ctx,
        View content,
        float initialWidth,
        float minWidth = 140f,
        float maxWidth = 600f,
        Action<float>? onWidthChanged = null,
        Action? onSplitterDoubleClick = null)
    {
        var splitterHovered = new State<bool>(false);
        var splitter = new RectView();
        splitter.BindThemedBackgroundColor(ctx.Theme(), s =>
            splitterHovered.Value ? s.SidebarSplitter.Hover : s.SidebarSplitter.Idle);

        var sidebar = new ResizableLeftSidebar(content, splitter, initialWidth, minWidth, maxWidth)
        {
            WidthChanged = onWidthChanged,
        };

        splitter.UseController(ctx.Require<InputSystem>(), () => new SplitterController(
            ctx,
            DragAxis.X,
            sidebar.AdjustWidthByPixels,
            h => splitterHovered.Value = h,
            onSplitterDoubleClick));

        return sidebar;
    }
}
