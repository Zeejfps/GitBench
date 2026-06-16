using GitBench.Controls;
using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Widgets;

/// <summary>
/// Sidebar content with a draggable splitter on its right edge. Wraps the
/// <see cref="ResizableLeftSidebar"/> builder so it composes inside a widget tree; the
/// wrapper owns its width and persists changes through <see cref="OnWidthChanged"/>.
/// </summary>
public sealed record ResizableSidebar : Widget
{
    public required IWidget Content { get; init; }
    public required float InitialWidth { get; init; }
    public float MinResizeWidth { get; init; } = 140f;
    public float MaxResizeWidth { get; init; } = 600f;
    public Action<float>? OnWidthChanged { get; init; }

    protected override View CreateView(Context ctx) => ResizableLeftSidebar.Build(
        ctx,
        Content.BuildView(ctx),
        initialWidth: InitialWidth,
        minWidth: MinResizeWidth,
        maxWidth: MaxResizeWidth,
        onWidthChanged: OnWidthChanged);
}
