using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Controls;

// The host-facing slice of a sliding selection bar: its eased progress, a revision bump for non-tween
// repaints, and the current bar rect. Lets the overlay host stay key-agnostic.
internal interface ISelectionBar
{
    IReadable<float> Progress { get; }
    IReadable<int> Revision { get; }
    bool TryGetRect(out RectF rect);
}

// Slides one selection bar between the rows of a grouped, variable-height widget tree (the repo bar and
// the branches sidebar). There is no row index to lerp — rows publish their live laid-out rect, the
// active row is resolved from an injected key readable, and the bar lerps between the previous and
// current row's rects. Reading the rects live each frame keeps the bar glued to the rows as the list
// scrolls (the scroll pane scrolls by re-layout, so a row's Position already reflects scroll).
//
// TKey is the row identity the selection is keyed on (a repo id, a branch row key). The bar subscribes
// to <paramref name="active"/> for the current key but does not own it — the caller disposes the readable.
internal sealed class TreeSelectionBar<TKey> : ISelectionBar, IDisposable where TKey : struct
{
    private readonly Tween _tween;
    private readonly IDisposable _activeSub;
    private readonly Dictionary<TKey, Func<RectF>> _providers = new();
    private readonly State<int> _revision = new(0);

    private TKey? _activeKey;
    private Func<RectF>? _from;
    private Func<RectF>? _to;

    public TreeSelectionBar(IFrameTicker ticker, IReadable<TKey?> active)
    {
        _tween = new Tween(ticker, 0.18f, Easings.EaseOutCubic, maxStep: 0.033f);
        _activeSub = active.Subscribe(SetActive);
    }

    // Eased slide progress; the host repaints while this ticks.
    public IReadable<float> Progress => _tween.Progress;

    // Bumped on snap/clear/register so the host repaints even when no tween is running.
    public IReadable<int> Revision => _revision;

    // A row reports the rect it currently occupies. The closure is read live, so it tracks the row
    // through scroll and relayout. The token unregisters the row when it unmounts.
    public IDisposable Register(TKey key, Func<RectF> liveRect)
    {
        _providers[key] = liveRect;
        if (IsActive(key))
            Snap(liveRect);
        return new Registration(this, key, liveRect);
    }

    private void Unregister(TKey key, Func<RectF> liveRect)
    {
        if (_providers.TryGetValue(key, out var current) && ReferenceEquals(current, liveRect))
            _providers.Remove(key);
        if (IsActive(key))
        {
            _from = null;
            _to = null;
            _revision.Value++;
        }
    }

    private bool IsActive(TKey key) =>
        _activeKey is { } a && EqualityComparer<TKey>.Default.Equals(a, key);

    private void SetActive(TKey? key)
    {
        _activeKey = key;
        var next = key is { } k && _providers.TryGetValue(k, out var provider) ? provider : null;
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

    private sealed class Registration(TreeSelectionBar<TKey> owner, TKey key, Func<RectF> liveRect) : IDisposable
    {
        public void Dispose() => owner.Unregister(key, liveRect);
    }
}

// Draws the sliding selection bar behind its child rows, clipped to the scroll viewport: a full-width
// fill with a 2px accent down the leading edge — the look the repo bar established and the branches
// sidebar shares.
internal sealed class TreeSelectionOverlayHost : ContainerView
{
    private const float AccentBarWidth = 2f;

    private readonly ISelectionBar _bar;
    private RowSelectionStyles _styles = ThemeStyles.Dark.RowSelection;

    public TreeSelectionOverlayHost(ISelectionBar bar, IThemeService<ThemeStyles> theme)
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

// Hosts scrollable tree content and floats the selection bar behind it. Provides the shared bar to the
// subtree so each row can register its rect, and owns the bar's lifetime.
internal sealed record TreeSelectionOverlay<TKey> : Widget where TKey : struct
{
    public required TreeSelectionBar<TKey> Bar { get; init; }
    public required IWidget Child { get; init; }

    protected override View CreateView(Context ctx)
    {
        var host = new TreeSelectionOverlayHost(Bar, ctx.Theme());
        var scope = new Context(ctx);
        scope.AddService(Bar);
        host.Children.Add(Child.BuildView(scope));
        host.Use(() => Bar);
        return host;
    }
}
