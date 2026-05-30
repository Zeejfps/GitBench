using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Wraps a sidebar content view and lays a draggable splitter strip on its right edge.
/// The wrapper itself owns the sidebar's width via <see cref="View.Width"/>, so
/// the surrounding <c>BorderLayoutView</c> reads the new width on each layout pass after
/// a drag. Width is clamped to <see cref="MinWidth"/> / <see cref="MaxWidth"/>.
/// </summary>
internal sealed class ResizableLeftSidebar : MultiChildView
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

    // Positive dx = mouse moved right = sidebar grows. Negative shrinks. Clamping keeps
    // the sidebar usable at both extremes (it can't disappear or eat the main view).
    public void AdjustWidthByPixels(float dx)
    {
        var newWidth = Math.Clamp((float)Width + dx, _minWidth, _maxWidth);
        if (Math.Abs(newWidth - (float)Width) < 0.5f) return;
        Width = newWidth;
        WidthChanged?.Invoke(newWidth);
    }

    protected override void OnLayoutChildren()
    {
        var pos = Position;
        if (pos.Width <= 0f || pos.Height <= 0f) return;

        var contentWidth = Math.Max(0f, pos.Width - SplitterThickness);

        _content.LeftConstraint = pos.Left;
        _content.BottomConstraint = pos.Bottom;
        _content.WidthConstraint = contentWidth;
        _content.HeightConstraint = pos.Height;
        _content.LayoutSelf();

        _splitter.LeftConstraint = pos.Left + contentWidth;
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
        View content,
        float initialWidth,
        float minWidth = 140f,
        float maxWidth = 600f,
        Action<float>? onWidthChanged = null)
    {
        var splitterHovered = new State<bool>(false);
        var splitter = new RectView();
        splitter.BindThemedBackgroundColor(s =>
            splitterHovered.Value ? s.SidebarSplitter.Hover : s.SidebarSplitter.Idle);

        var sidebar = new ResizableLeftSidebar(content, splitter, initialWidth, minWidth, maxWidth)
        {
            WidthChanged = onWidthChanged,
        };

        splitter.UseController(ctx => new SplitterController(
            ctx,
            DragAxis.X,
            sidebar.AdjustWidthByPixels,
            h => splitterHovered.Value = h));

        return sidebar;
    }
}
