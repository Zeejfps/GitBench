using GitBench.Controls;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Widgets;

/// <summary>Which edge of the main content a sidebar hangs off, in writing direction.</summary>
public enum SidebarEdge
{
    /// <summary>The inline-start side — left under LTR — with the splitter on its inline-end edge.</summary>
    Leading,

    /// <summary>The inline-end side — right under LTR — with the splitter on its inline-start edge.</summary>
    Trailing,
}

/// <summary>
/// Sidebar content with a draggable splitter strip on the edge that faces the main content. The
/// wrapper owns the sidebar's width via <see cref="View.Width"/>, so the surrounding
/// <c>BorderLayoutView</c> reads the new width on each layout pass after a drag, and persists changes
/// through <see cref="OnWidthChanged"/>.
/// </summary>
public sealed record ResizableSidebar : Widget
{
    public SidebarEdge Edge { get; init; } = SidebarEdge.Leading;
    public required IWidget Content { get; init; }
    public required float InitialWidth { get; init; }
    public float MinResizeWidth { get; init; } = 140f;
    public float MaxResizeWidth { get; init; } = 600f;

    /// <summary>What the sidebar leaves of the window's width however far it is dragged, so it can't
    /// crowd out what sits beside it.</summary>
    public float MinRemainingWidth { get; init; }
    public Action<float>? OnWidthChanged { get; init; }
    public Action? OnSplitterDoubleClick { get; init; }

    protected override View CreateView(Context ctx)
    {
        var splitterHovered = new State<bool>(false);
        var splitter = new RectView();
        splitter.BindThemedBackgroundColor(ctx.Theme(), s =>
            splitterHovered.Value ? s.SidebarSplitter.Hover : s.SidebarSplitter.Idle);

        var sidebar = new ResizableSidebarView(Content.BuildView(ctx), splitter, InitialWidth, MinResizeWidth, MaxResizeWidth)
        {
            WidthChanged = OnWidthChanged,
            SplitterAtInlineStart = Edge == SidebarEdge.Trailing,
            MinRemainingWidth = MinRemainingWidth,
        };

        splitter.UseController(ctx.Require<InputSystem>(), () => new SplitterController(
            ctx,
            DragAxis.X,
            sidebar.AdjustWidthByPixels,
            h => splitterHovered.Value = h,
            OnSplitterDoubleClick));

        return sidebar;
    }
}

internal sealed class ResizableSidebarView : ContainerView
{
    private const float SplitterThickness = 5f;

    private readonly View _content;
    private readonly View _splitter;
    private readonly float _minWidth;
    private readonly float _maxWidth;

    public Action<float>? WidthChanged { get; init; }
    public bool SplitterAtInlineStart { get; init; }
    public float MinRemainingWidth { get; init; }

    public ResizableSidebarView(View content, View splitter, float initialWidth, float minWidth, float maxWidth)
    {
        _content = content;
        _splitter = splitter;
        _minWidth = minWidth;
        _maxWidth = maxWidth;
        Width = Math.Clamp(initialWidth, _minWidth, _maxWidth);
        AddChildToSelf(_content);
        AddChildToSelf(_splitter);
    }

    // The inline-end edge is the right one under LTR; the inline-start edge is the right one under RTL.
    private bool SplitterOnRight => SplitterAtInlineStart == IsRtl;

    // Dragging the splitter toward the main content grows the sidebar. With the splitter on the
    // right a rightward move (positive dx) grows it; on the left the sense flips. Clamping keeps the
    // sidebar usable at both extremes (it can't disappear or eat the main view).
    public void AdjustWidthByPixels(float dx)
    {
        var signed = SplitterOnRight ? dx : -dx;
        var newWidth = Math.Clamp((float)Width + signed, _minWidth, MaxWidthNow());
        if (Math.Abs(newWidth - (float)Width) < 0.5f) return;
        Width = newWidth;
        WidthChanged?.Invoke(newWidth);
    }

    private float MaxWidthNow()
    {
        if (MinRemainingWidth <= 0f) return _maxWidth;
        View root = this;
        while (root.Parent is { } parent) root = parent;
        return Math.Max(_minWidth, Math.Min(_maxWidth, root.Position.Width - MinRemainingWidth));
    }

    protected override void OnLayoutChildren()
    {
        var pos = Position;
        if (pos.Width <= 0f || pos.Height <= 0f) return;

        var contentWidth = Math.Max(0f, pos.Width - SplitterThickness);
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
}
