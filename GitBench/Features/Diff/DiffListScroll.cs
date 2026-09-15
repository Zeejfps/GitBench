using ZGF.Gui.Desktop.Components.VirtualRowList;

namespace GitBench.Features.Diff;

/// <summary>
/// The scroll state a diff row list carries beside its <see cref="VirtualRowListView"/>: the
/// horizontal offset the rows draw at, the scrollbar sync for it, and a vertical target that is
/// re-asserted for a few frames after it is set. Setting a non-zero vertical scroll right as
/// content changes can be clobbered — when taller content makes the scrollbar transition
/// hidden→visible, the bar's layout echoes a stale position back through the sync controller —
/// so the target re-applies until it sticks or the budget runs out, then hands scrolling back
/// to the user.
/// </summary>
internal sealed class DiffListScroll
{
    private const int TargetFrames = 8;

    private readonly VirtualRowListView _list;
    private readonly Func<float> _contentWidth;
    private readonly Func<float> _viewportWidth;
    private float? _pendingY;
    private int _pendingFrames;
    private float _lastNormalizedX;
    // Sentinel start so the very first Publish fires the event even when the computed scale equals
    // 1: the scrollbar thumb's built-in default is Scale=0.5, and without an explicit "scale=1,
    // hide" message it stays visible at half width until something else forces a change.
    private float _lastHorizontalScale = -1f;

    public DiffListScroll(VirtualRowListView list, Func<float> contentWidth, Func<float> viewportWidth)
    {
        _list = list;
        _contentWidth = contentWidth;
        _viewportWidth = viewportWidth;
    }

    public float X { get; private set; }

    public float ContentWidth => _contentWidth();

    public float HorizontalScale { get; private set; } = 1f;

    public event Action<float>? HorizontalScrollPositionChanged;

    public void SetTarget(float y)
    {
        _pendingY = y;
        _pendingFrames = TargetFrames;
        _list.SetScrollY(y);
    }

    public void ReassertTarget()
    {
        if (_pendingY is not float want) return;
        var max = Math.Max(0f, _list.ContentHeight - _list.Position.Height);
        var clamped = Math.Clamp(want, 0f, max);
        if (Math.Abs(_list.ScrollY - clamped) <= 0.5f || --_pendingFrames < 0)
        {
            _pendingY = null;
            return;
        }
        _list.SetScrollY(clamped);
    }

    public bool ScrollXBy(float delta)
    {
        var prev = X;
        X += delta;
        ClampX();
        if (X == prev) return false;
        PublishX();
        return true;
    }

    public void SetNormalizedX(float normalized)
    {
        var range = _contentWidth() - _viewportWidth();
        X = range <= 0 ? 0f : Math.Clamp(normalized, 0f, 1f) * range;
    }

    public void ClampX()
    {
        var maxX = Math.Max(0f, _contentWidth() - _viewportWidth());
        if (X < 0f) X = 0f;
        else if (X > maxX) X = maxX;
    }

    /// <summary><paramref name="viewportFits"/> forces the "nothing to scroll" report for content
    /// that is not being drawn as rows.</summary>
    public void PublishX(bool viewportFits = false)
    {
        float normalizedX, scale;
        var contentW = _contentWidth();
        var vpw = _viewportWidth();
        if (viewportFits || contentW <= vpw || vpw <= 0)
        {
            scale = 1f;
            normalizedX = 0f;
        }
        else
        {
            scale = vpw / contentW;
            normalizedX = Math.Clamp(X / (contentW - vpw), 0f, 1f);
        }

        HorizontalScale = scale;
        if (Math.Abs(scale - _lastHorizontalScale) <= 0.0001f && Math.Abs(normalizedX - _lastNormalizedX) <= 0.0001f)
            return;
        _lastHorizontalScale = scale;
        _lastNormalizedX = normalizedX;
        HorizontalScrollPositionChanged?.Invoke(normalizedX);
    }

    public void RestoreX(float x) => X = x;
}
