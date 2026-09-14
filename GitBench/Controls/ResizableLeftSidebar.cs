using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitBench.Controls;

/// <summary>
/// Wraps a sidebar content view and lays a draggable splitter strip on the edge facing the main
/// content: the inline-end edge for a leading sidebar (its right edge under LTR, its left under RTL),
/// the inline-start edge for a trailing one. The wrapper itself owns the sidebar's width via
/// <see cref="View.Width"/>, so the surrounding <c>BorderLayoutView</c> reads the new width on each
/// layout pass after a drag. Width is clamped to <see cref="_minWidth"/> / <see cref="_maxWidth"/>.
/// </summary>
internal sealed class ResizableLeftSidebar : ContainerView
{
    private const float SplitterThickness = 5f;

    private readonly View _content;
    private readonly View _splitter;
    private readonly float _minWidth;
    private readonly float _maxWidth;
    private readonly bool _splitterAtInlineStart;

    public Action<float>? WidthChanged { get; set; }

    public ResizableLeftSidebar(
        View content, View splitter, float initialWidth, float minWidth, float maxWidth, bool splitterAtInlineStart = false)
    {
        _content = content;
        _splitter = splitter;
        _minWidth = minWidth;
        _maxWidth = maxWidth;
        _splitterAtInlineStart = splitterAtInlineStart;
        Width = Math.Clamp(initialWidth, _minWidth, _maxWidth);
        AddChildToSelf(_content);
        AddChildToSelf(_splitter);
    }

    // Dragging the splitter toward the main content grows the sidebar. With the splitter on the
    // right a rightward move (positive dx) grows it; on the left the sense flips. Clamping keeps the
    // sidebar usable at both extremes (it can't disappear or eat the main view).
    public void AdjustWidthByPixels(float dx)
    {
        var signed = SplitterOnRight ? dx : -dx;
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
        // The splitter takes the edge facing the main content, with the content shifted over to make
        // room when that is the left one.
        var onRight = SplitterOnRight;
        var contentLeft = onRight ? pos.Left : pos.Left + SplitterThickness;
        var splitterLeft = onRight ? pos.Left + contentWidth : pos.Left;

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

    // The inline-end edge is the right one under LTR; the inline-start edge is the right one under RTL.
    private bool SplitterOnRight => _splitterAtInlineStart == IsRtl;

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
        Action? onSplitterDoubleClick = null,
        bool splitterAtInlineStart = false)
    {
        var splitterHovered = new State<bool>(false);
        var splitter = new RectView();
        splitter.BindThemedBackgroundColor(ctx.Theme(), s =>
            splitterHovered.Value ? s.SidebarSplitter.Hover : s.SidebarSplitter.Idle);

        var sidebar = new ResizableLeftSidebar(content, splitter, initialWidth, minWidth, maxWidth, splitterAtInlineStart)
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
