using ZGF.Gui;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Stacks a top view and (optionally) a bottom view separated by a draggable horizontal
/// splitter. The split is tracked as a fraction of the available height so window resizes
/// scale both halves; the splitter controller calls <see cref="AdjustBottomFractionByPixels"/>
/// to nudge the fraction during a drag.
/// </summary>
internal sealed class VerticalSplitContainer : MultiChildView
{
    private const float SplitterThickness = 5f;
    private const float MinFraction = 0.1f;
    private const float MaxFraction = 0.9f;

    private readonly View _top;
    private readonly View _bottom;
    private readonly View _splitter;
    private bool _bottomVisible;
    private bool _bottomCollapsed;
    private float _collapsedBottomHeight;
    private float _bottomFraction;

    public VerticalSplitContainer(View top, View bottom, View splitter, float bottomFraction)
    {
        _top = top;
        _bottom = bottom;
        _splitter = splitter;
        _bottomFraction = Math.Clamp(bottomFraction, MinFraction, MaxFraction);
        AddChildToSelf(_top);
    }

    public bool BottomVisible
    {
        get => _bottomVisible;
        set
        {
            if (_bottomVisible == value) return;
            _bottomVisible = value;
            SyncBottomChildren();
            SetDirty();
        }
    }

    // When true, the bottom panel shows at a fixed pixel height (CollapsedBottomHeight)
    // with no splitter — used by callers that want to keep a thin "header" of the bottom
    // panel visible while hiding its body. Has no effect while BottomVisible is false.
    public void SetBottomCollapsed(bool collapsed, float collapsedHeight)
    {
        var heightChanged = Math.Abs(_collapsedBottomHeight - collapsedHeight) > 0.01f;
        if (_bottomCollapsed == collapsed && !heightChanged) return;
        _bottomCollapsed = collapsed;
        _collapsedBottomHeight = collapsedHeight;
        SyncBottomChildren();
        SetDirty();
    }

    public void BindBottomVisible(IReadable<bool> source)
        => source.Subscribe(v => BottomVisible = v);

    public void BindBottomVisible(Func<bool> compute)
        => new Derived<bool>(compute).Subscribe(v => BottomVisible = v);

    public void BindBottomCollapsed(IReadable<bool> source, float collapsedHeight)
        => source.Subscribe(c => SetBottomCollapsed(c, collapsedHeight));

    // Positive dy = mouse moved up (Y-up coords). Up = bigger bottom (diff grows), down =
    // smaller bottom. Clamped so neither side can collapse to zero.
    public void AdjustBottomFractionByPixels(float dy)
    {
        if (!_bottomVisible || _bottomCollapsed) return;
        var available = Position.Height - SplitterThickness;
        if (available <= 0f) return;
        _bottomFraction = Math.Clamp(_bottomFraction + dy / available, MinFraction, MaxFraction);
        SetDirty();
    }

    private void SyncBottomChildren()
    {
        var wantSplitter = _bottomVisible && !_bottomCollapsed;
        var wantBottom = _bottomVisible;

        var hasSplitter = ReferenceEquals(_splitter.Parent, this);
        var hasBottom = ReferenceEquals(_bottom.Parent, this);

        if (wantSplitter && !hasSplitter) AddChildToSelf(_splitter);
        else if (!wantSplitter && hasSplitter) RemoveChildFromSelf(_splitter);

        if (wantBottom && !hasBottom) AddChildToSelf(_bottom);
        else if (!wantBottom && hasBottom) RemoveChildFromSelf(_bottom);
    }

    protected override void OnLayoutChildren()
    {
        var pos = Position;
        if (pos.Width <= 0f || pos.Height <= 0f) return;

        if (!_bottomVisible)
        {
            LayoutSlice(_top, pos.Left, pos.Bottom, pos.Width, pos.Height);
            return;
        }

        if (_bottomCollapsed)
        {
            var bottomH = Math.Min(_collapsedBottomHeight, pos.Height);
            var topH = Math.Max(0f, pos.Height - bottomH);
            LayoutSlice(_top, pos.Left, pos.Bottom + bottomH, pos.Width, topH);
            LayoutSlice(_bottom, pos.Left, pos.Bottom, pos.Width, bottomH);
            return;
        }

        var available = Math.Max(0f, pos.Height - SplitterThickness);
        var bottomH2 = available * _bottomFraction;
        var topH2 = available - bottomH2;

        LayoutSlice(_top, pos.Left, pos.Bottom + bottomH2 + SplitterThickness, pos.Width, topH2);
        LayoutSlice(_splitter, pos.Left, pos.Bottom + bottomH2, pos.Width, SplitterThickness);
        LayoutSlice(_bottom, pos.Left, pos.Bottom, pos.Width, bottomH2);
    }

    private static void LayoutSlice(View child, float left, float bottom, float width, float height)
    {
        child.LeftConstraint = left;
        child.BottomConstraint = bottom;
        child.WidthConstraint = width;
        child.HeightConstraint = height;
        child.LayoutSelf();
    }
}