using GitBench.Controls;
using GitBench.Features.Repos;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.App;

/// <summary>
/// Left rail listing repositories: the full bar with a draggable splitter whose width persists,
/// or the compact icon rail while collapsed. Toggling slides the sidebar's real width between the
/// two, so the main content reflows with the animation.
/// </summary>
internal sealed record RepoBarSidebar : Widget
{
    private const float SlideSeconds = 0.2f;

    protected override View CreateView(Context ctx)
    {
        var preferences = ctx.Require<PreferencesService>();
        var collapse = ctx.Require<RepoBarCollapseState>();

        // The bar unmounts only once it is parked collapsed (hidden behind the fully opaque rail)
        // and remounts up front when expanding, so the rail crossfades over live bar content in
        // both directions.
        var showBar = new State<bool>(!collapse.IsCollapsed.Value);

        SidebarRevealHost host = null!;
        var content = new Switch<bool>
        {
            Value = showBar,
            Case = bar => bar
                ? new ResizableSidebar
                {
                    Content = new RepoBar(),
                    InitialWidth = preferences.Current.RepoBarWidth,
                    MinResizeWidth = 220f,
                    OnWidthChanged = width =>
                    {
                        preferences.SetRepoBarWidth(width);
                        host.SetRestingWidth(width);
                    },
                    OnSplitterDoubleClick = collapse.Toggle,
                }
                : (IWidget)new Empty(),
        }.BuildView(ctx);

        var rail = new RepoRail().BuildView(ctx);
        var initialWidth = collapse.IsCollapsed.Value ? RepoRail.RailWidth : preferences.Current.RepoBarWidth;
        host = new SidebarRevealHost(content, rail, initialWidth);

        var tween = new Tween(ctx.Require<IFrameTicker>(), SlideSeconds, Easings.EaseOutCubic, maxStep: 0.033f);
        float from = initialWidth, to = initialWidth;
        host.Bind(tween.Progress, p => host.SetWidth(from + (to - from) * p));
        tween.Completed += () =>
        {
            if (!collapse.IsCollapsed.Value) return;
            showBar.Value = false;
            host.ContentWidth = RepoRail.RailWidth;
        };

        var wasCollapsed = collapse.IsCollapsed.Value;
        host.Bind(collapse.IsCollapsed, nowCollapsed =>
        {
            if (nowCollapsed == wasCollapsed) return;
            wasCollapsed = nowCollapsed;
            from = (float)host.Width;
            to = nowCollapsed ? RepoRail.RailWidth : preferences.Current.RepoBarWidth;
            if (!nowCollapsed)
            {
                showBar.Value = true;
                host.ContentWidth = to;
            }
            tween.Restart();
        });

        host.Use(() => tween);
        return host;
    }
}

/// <summary>
/// The sidebar's sliding shell: it owns the width the surrounding layout sees and stacks two
/// layers — the bar content, laid out at <see cref="ContentWidth"/> anchored to the window-edge
/// side and clipped at the content-facing edge (revealed/concealed mid-slide, never squished),
/// and the icon rail on top, crossfading in as the width nears rail size.
/// </summary>
internal sealed class SidebarRevealHost : ContainerView
{
    private const float RailFadeBand = 60f;

    private readonly View _content;
    private readonly View _rail;

    public SidebarRevealHost(View content, View rail, float initialWidth)
    {
        _content = content;
        _rail = rail;
        ContentWidth = initialWidth;
        AddChildToSelf(content);
        AddChildToSelf(rail);
        SetWidth(initialWidth);
    }

    /// <summary>The width the bar layer lays out at — held at the bar's full width during the slide.</summary>
    public float ContentWidth
    {
        get;
        set => SetField(ref field, value);
    }

    /// <summary>A splitter drag while parked expanded moves both widths in step.</summary>
    public void SetRestingWidth(float width)
    {
        ContentWidth = width;
        SetWidth(width);
    }

    /// <summary>
    /// The rail's opacity is a function of the width alone — not of slide progress — so the
    /// crossfade stays continuous when a toggle reverses the slide midway.
    /// </summary>
    public void SetWidth(float width)
    {
        Width = width;
        var opacity = Math.Clamp((RepoRail.RailWidth + RailFadeBand - width) / RailFadeBand, 0f, 1f);
        _rail.Opacity = opacity;
        _rail.IsVisible = opacity > 0f;
    }

    public override bool ClipsContent => true;

    protected override void OnLayoutChildren()
    {
        var pos = Position;
        if (pos.Width <= 0f || pos.Height <= 0f) return;

        // Both layers anchor to the window edge (the right edge under RTL), so the clip edge is
        // the one that moves.
        _content.LeftConstraint = IsRtl ? pos.Right - ContentWidth : pos.Left;
        _content.BottomConstraint = pos.Bottom;
        _content.WidthConstraint = ContentWidth;
        _content.HeightConstraint = pos.Height;
        _content.LayoutSelf();

        _rail.LeftConstraint = IsRtl ? pos.Right - RepoRail.RailWidth : pos.Left;
        _rail.BottomConstraint = pos.Bottom;
        _rail.WidthConstraint = RepoRail.RailWidth;
        _rail.HeightConstraint = pos.Height;
        _rail.LayoutSelf();
    }

    protected override void OnDrawChildren(ICanvas c)
    {
        c.PushClip(Position);
        base.OnDrawChildren(c);
        c.PopClip();
    }
}
