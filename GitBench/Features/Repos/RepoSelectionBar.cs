using GitBench.Controls;
using GitBench.Git;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Repos;

// Slides one selection bar between repo rows, the same motion the branches/files/history lists use.
// Those are flat uniform-height virtual lists, so their bar is a single floated overlay whose pixel
// position is index*RowHeight lerped by a tween. The RepoBar is a grouped, variable-height widget
// tree instead, so there is no index to lerp — rows publish their live laid-out rect here, the active
// row is resolved from IRepoRegistry.Active, and the bar lerps between the previous and current row's
// rects. Reading the rects live each frame keeps the bar glued to the rows as the list scrolls
// (VerticalScrollPane scrolls by re-layout, so a row's Position already reflects scroll).
internal sealed class RepoSelectionBar : IDisposable
{
    private readonly Tween _tween;
    private readonly IDisposable _activeSub;
    private readonly Dictionary<Guid, Func<RectF>> _providers = new();
    private readonly State<int> _revision = new(0);

    private Guid? _activeId;
    private Func<RectF>? _from;
    private Func<RectF>? _to;

    public RepoSelectionBar(IFrameTicker ticker, IRepoRegistry registry)
    {
        _tween = new Tween(ticker, 0.18f, Easings.EaseOutCubic, maxStep: 0.033f);
        _activeSub = registry.Active.Subscribe(repo => SetActive(repo?.Id));
    }

    // Eased slide progress; the host repaints while this ticks.
    public IReadable<float> Progress => _tween.Progress;

    // Bumped on snap/clear/register so the host repaints even when no tween is running.
    public IReadable<int> Revision => _revision;

    // A row reports the rect it currently occupies. The closure is read live, so it tracks the row
    // through scroll and relayout. The token unregisters the row when it unmounts.
    public IDisposable Register(Guid repoId, Func<RectF> liveRect)
    {
        _providers[repoId] = liveRect;
        if (_activeId == repoId)
            Snap(liveRect);
        return new Registration(this, repoId, liveRect);
    }

    private void Unregister(Guid repoId, Func<RectF> liveRect)
    {
        if (_providers.TryGetValue(repoId, out var current) && ReferenceEquals(current, liveRect))
            _providers.Remove(repoId);
        if (_activeId == repoId)
        {
            _from = null;
            _to = null;
            _revision.Value++;
        }
    }

    private void SetActive(Guid? id)
    {
        _activeId = id;
        var next = id is { } gid && _providers.TryGetValue(gid, out var provider) ? provider : null;
        if (next == null)
        {
            _from = null;
            _to = null;
            _revision.Value++;
            return;
        }

        if (_to == null)
            Snap(next);
        else
        {
            _from = _to;
            _to = next;
            _tween.Restart();
        }
    }

    private void Snap(Func<RectF> provider)
    {
        _from = provider;
        _to = provider;
        _revision.Value++;
    }

    public bool TryGetRect(out RectF rect)
    {
        rect = default;
        if (_to == null) return false;

        var to = _to();
        if (to.Height <= 0f) return false;

        if (_from == null || ReferenceEquals(_from, _to))
        {
            rect = to;
            return true;
        }

        var from = _from();
        rect = from.Height <= 0f ? to : Lerp(from, to, _tween.Progress.Value);
        return true;
    }

    private static RectF Lerp(RectF a, RectF b, float t) => new(
        a.Left + (b.Left - a.Left) * t,
        a.Bottom + (b.Bottom - a.Bottom) * t,
        a.Width + (b.Width - a.Width) * t,
        a.Height + (b.Height - a.Height) * t);

    public void Dispose()
    {
        _activeSub.Dispose();
        _tween.Dispose();
    }

    private sealed class Registration(RepoSelectionBar owner, Guid repoId, Func<RectF> liveRect) : IDisposable
    {
        public void Dispose() => owner.Unregister(repoId, liveRect);
    }
}

// Draws the sliding selection bar behind its child rows, clipped to the scroll viewport. Keeps the
// RepoBar's established active-row look: a full-width fill with a 2px accent down the leading edge.
internal sealed class RepoSelectionOverlayHost : ContainerView
{
    private const float AccentBarWidth = 2f;

    private readonly RepoSelectionBar _bar;
    private RowSelectionStyles _styles = ThemeStyles.Dark.RowSelection;

    public RepoSelectionOverlayHost(RepoSelectionBar bar, IThemeService<ThemeStyles> theme)
    {
        _bar = bar;
        this.BindThemed(theme, s => { _styles = s.RowSelection; SetDirty(); });
        this.Bind(bar.Progress, _ => SetDirty());
        this.Bind(bar.Revision, _ => SetDirty());
    }

    protected override void OnDrawSelf(ICanvas c)
    {
        if (!_bar.TryGetRect(out var row)) return;

        var z = GetDrawZIndex();
        c.PushClip(Position);

        c.DrawRect(new DrawRectInputs
        {
            Position = row,
            Style = new RectStyle { BackgroundColor = _styles.Fill },
            ZIndex = z,
        });

        var accentLeft = IsRtl ? row.Right - AccentBarWidth : row.Left;
        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(accentLeft, row.Bottom, AccentBarWidth, row.Height),
            Style = new RectStyle { BackgroundColor = _styles.AccentBar },
            ZIndex = z,
        });

        c.PopClip();
    }
}

// Hosts the scrollable repo content and floats the selection bar behind it. Provides the shared
// RepoSelectionBar to the subtree so each row can register its rect, and owns the bar's lifetime.
internal sealed record RepoSelectionOverlay : Widget
{
    public required RepoSelectionBar Bar { get; init; }
    public required IWidget Child { get; init; }

    protected override View CreateView(Context ctx)
    {
        var host = new RepoSelectionOverlayHost(Bar, ctx.Theme());
        var scope = new Context(ctx);
        scope.AddService(Bar);
        host.Children.Add(Child.BuildView(scope));
        host.Use(() => Bar);
        return host;
    }
}
