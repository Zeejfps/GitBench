using GitBench.Theming;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Controls;

// One floating selection bar for a fixed-row-height virtual list. The bar sits on a row index and
// slides between two real rows; first-select, clear, and any move from or to "no row" snap in place
// (sliding in from nowhere reads as a glitch). The host repaints while Progress ticks and paints the
// bar through Draw from its list's selection-overlay hook so it rides scroll.
internal sealed class ListSelectionBar : IDisposable
{
    private readonly Tween _tween;
    private float _from;
    private float _to;

    public ListSelectionBar(IFrameTicker ticker)
    {
        _tween = new Tween(ticker, 0.18f, Easings.EaseOutCubic);
    }

    public IReadable<float> Progress => _tween.Progress;

    public int Index { get; private set; } = -1;

    public void MoveTo(int index)
    {
        if (index == Index) return;
        if (Index >= 0 && index >= 0)
        {
            _from = Current;
            _to = index;
            Index = index;
            _tween.Restart();
            return;
        }
        Snap(index);
    }

    public void Snap(int index)
    {
        Index = index;
        _from = index < 0 ? 0f : index;
        _to = _from;
    }

    private float Current => _from + (_to - _from) * _tween.Progress.Value;

    public void Draw(ICanvas c, RectF viewport, float scrollY, float rowHeight, RowSelectionStyles styles, int z, bool isRtl)
    {
        if (Index < 0) return;
        var rowTop = viewport.Top + scrollY - Current * rowHeight;
        var rowRect = new RectF(viewport.Left, rowTop - rowHeight, viewport.Width, rowHeight);
        RowSelection.DrawBackground(c, rowRect, isSelected: true, isHovered: false, styles, z, isRtl: isRtl);
    }

    public void Dispose() => _tween.Dispose();
}
