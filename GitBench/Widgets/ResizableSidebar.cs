using GitBench.Controls;
using ZGF.Gui;
using ZGF.Gui.Widgets;

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
/// Sidebar content with a draggable splitter on the edge that faces the main content. Wraps the
/// <see cref="ResizableLeftSidebar"/> builder so it composes inside a widget tree; the
/// wrapper owns its width and persists changes through <see cref="OnWidthChanged"/>.
/// </summary>
public sealed record ResizableSidebar : Widget
{
    public required IWidget Content { get; init; }
    public required float InitialWidth { get; init; }
    public SidebarEdge Edge { get; init; } = SidebarEdge.Leading;
    public float MinResizeWidth { get; init; } = 140f;
    public float MaxResizeWidth { get; init; } = 600f;
    public Action<float>? OnWidthChanged { get; init; }
    public Action? OnSplitterDoubleClick { get; init; }

    protected override View CreateView(Context ctx) => ResizableLeftSidebar.Build(
        ctx,
        Content.BuildView(ctx),
        initialWidth: InitialWidth,
        minWidth: MinResizeWidth,
        maxWidth: MaxResizeWidth,
        onWidthChanged: OnWidthChanged,
        onSplitterDoubleClick: OnSplitterDoubleClick,
        splitterAtInlineStart: Edge == SidebarEdge.Trailing);
}
