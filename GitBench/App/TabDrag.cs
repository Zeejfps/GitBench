using GitBench.Controls;
using GitBench.Widgets;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.App;

/// <summary>Where a dragged tab would land, and the line the strip draws to say so.</summary>
/// <param name="InsertIndex">The slot it would take in the run as the run stands now — so a tab
/// moving rightwards names a slot one past where it ends up, the way an insertion index does.</param>
internal sealed record TabDropTarget(int InsertIndex, RectF Indicator);

/// <summary>
/// Reordering the content panel's tabs by dragging one along the strip.
/// </summary>
/// <remarks>
/// One of these for the application rather than one per strip, so the line it draws can live at the
/// top of the window with the repo bar's — a drop indicator inside the strip would be a two-pixel
/// bar inside a thirty-two pixel box, competing with the tab it is drawn over. The strip binds its
/// own run in while it is mounted, which is the only per-repository thing here.
/// </remarks>
internal sealed class TabDrag
{
    private const float IndicatorWidth = 2f;

    private readonly Dictionary<View, ContentTab> _tabs = new();

    private ContentTabRun? _run;

    /// <summary>The tab being dragged, or null when nothing is.</summary>
    public State<ContentTab?> Dragging { get; } = new(null);

    public State<TabDropTarget?> Target { get; } = new(null);

    /// <summary>Points this at the mounted strip's run. Disposing the binding takes the run with
    /// it, so the strip has one thing to hand its lifetime to.</summary>
    public IDisposable Bind(ContentTabRun run)
    {
        _run = run;
        return new Binding(this, run);
    }

    /// <summary>One tab's end of the drag, which is all the shared chrome is given.</summary>
    public ITabDrag Handle(ContentTab tab) => new TabHandle(this, tab);

    private void Update(PointF at) => Target.Value = Resolve(at);

    private void Complete()
    {
        var source = Dragging.Value;
        var target = Target.Value;
        Cancel();

        if (source is null || target is null || _run is null) return;

        var from = _run.Tabs.IndexOf(source);
        if (from < 0) return;

        // An insertion index counts the slots as they are before the move; lifting the tab out
        // first shifts everything after it down one.
        var to = target.InsertIndex > from ? target.InsertIndex - 1 : target.InsertIndex;
        _run.Move(from, to);
    }

    private void Cancel()
    {
        Dragging.Value = null;
        Target.Value = null;
    }

    /// <summary>
    /// Which slot the pointer is over, and the line to draw at its leading edge.
    /// </summary>
    /// <remarks>
    /// Read off the laid-out tabs rather than off any measurement of its own: the tabs are as wide
    /// as their names, and only the views know how wide that turned out to be. A drop either side
    /// of where the tab already sits is no target at all, so hovering over its own place does not
    /// draw a line promising a move that would not happen.
    /// </remarks>
    private TabDropTarget? Resolve(PointF at)
    {
        if (_run is null || Dragging.Value is not { } source) return null;

        var from = _run.Tabs.IndexOf(source);
        if (from < 0) return null;

        var laid = new List<(int Index, RectF Rect)>();
        foreach (var (view, tab) in _tabs)
        {
            var index = _run.Tabs.IndexOf(tab);
            if (index >= 0) laid.Add((index, view.Position));
        }

        if (laid.Count == 0) return null;
        laid.Sort(static (a, b) => a.Index.CompareTo(b.Index));

        var insert = _run.Tabs.Count;
        var edge = laid[^1].Rect.Left + laid[^1].Rect.Width;
        var rect = laid[^1].Rect;

        foreach (var (index, bounds) in laid)
        {
            if (at.X >= bounds.Left + bounds.Width * 0.5f) continue;
            insert = index;
            edge = bounds.Left;
            rect = bounds;
            break;
        }

        if (insert == from || insert == from + 1) return null;

        return new TabDropTarget(
            insert,
            new RectF(edge - IndicatorWidth * 0.5f, rect.Bottom, IndicatorWidth, rect.Height));
    }

    private sealed class Binding(TabDrag drag, ContentTabRun run) : IDisposable
    {
        public void Dispose()
        {
            if (ReferenceEquals(drag._run, run))
            {
                drag.Cancel();
                drag._run = null;
            }

            run.Dispose();
        }
    }

    private sealed class TabHandle(TabDrag drag, ContentTab tab) : ITabDrag
    {
        public void Register(View view) => drag._tabs[view] = tab;

        public void Unregister(View view)
        {
            drag._tabs.Remove(view);
            if (ReferenceEquals(drag.Dragging.Value, tab)) drag.Cancel();
        }

        public void Start(PointF at)
        {
            drag.Dragging.Value = tab;
            drag.Update(at);
        }

        public void Update(PointF at) => drag.Update(at);

        public void Complete() => drag.Complete();

        public void Cancel() => drag.Cancel();
    }
}

/// <summary>
/// The line saying where a dragged tab would land. Drawn at the top of the window rather than
/// inside the strip, beside the repo bar's, and nothing at all when no tab is being dragged.
/// </summary>
internal sealed record TabDropIndicator : Widget
{
    protected override View CreateView(Context ctx) => new Core(ctx.Require<TabDrag>(), ctx);

    private sealed class Core : ContainerView
    {
        private readonly RectView _bar;
        private readonly TabDrag _drag;

        public Core(TabDrag drag, Context ctx)
        {
            _drag = drag;
            ZIndex = 900;
            _bar = new RectView { BorderRadius = BorderRadiusStyle.All(1) };
            _bar.BindThemedBackgroundColor(ctx.Theme(), s => s.Palette.Accent);
            this.Use(() => drag.Target.Subscribe(OnTargetChanged));
        }

        private void OnTargetChanged(TabDropTarget? target)
        {
            if (target is null)
            {
                if (Children.Contains(_bar)) Children.Remove(_bar);
                return;
            }

            if (!Children.Contains(_bar)) Children.Add(_bar);
            SetDirty();
        }

        protected override void OnLayoutChildren()
        {
            if (_drag.Target.Value is not { } target) return;

            var bounds = target.Indicator;
            _bar.LeftConstraint = bounds.Left;
            _bar.BottomConstraint = bounds.Bottom;
            _bar.WidthConstraint = bounds.Width;
            _bar.HeightConstraint = bounds.Height;
            _bar.LayoutSelf();
        }
    }
}
