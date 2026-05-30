using ZGF.Gui;
using ZGF.Gui.HorizontalScrollBar;
using ZGF.Gui.VerticalScrollBar;

namespace GitGui;

internal sealed class ScrollSyncController : IDisposable
{
    private readonly IScrollableContent _content;
    private readonly VerticalScrollBarView _vScrollBar;
    private readonly HorizontalScrollBarView? _hScrollBar;

    public ScrollSyncController(
        IScrollableContent content,
        VerticalScrollBarView vScrollBar,
        HorizontalScrollBarView? hScrollBar = null)
    {
        _content = content;
        _vScrollBar = vScrollBar;
        _hScrollBar = hScrollBar;

        _content.VerticalScrollPositionChanged += OnContentVerticalScroll;
        _vScrollBar.ScrollPositionChanged += _content.SetVerticalNormalizedScrollPosition;

        if (_hScrollBar != null)
        {
            _content.HorizontalScrollPositionChanged += OnContentHorizontalScroll;
            _hScrollBar.ScrollPositionChanged += _content.SetHorizontalNormalizedScrollPosition;
        }

        // Pull the content's current scale so the bar reflects "fits / hidden" state
        // even when no event has fired yet. Critical for views that detach + re-attach
        // (e.g. LocalChangesPanel inside a placeholder-swap parent): each re-attach
        // builds a fresh controller, and without this initial pull the bar would sit
        // at its built-in default (PreferredHeight=12, Scale=0.5) until something
        // unrelated triggered an event.
        ScrollBarSync.ApplyVertical(_vScrollBar, _content.VerticalScale, 0f);
        if (_hScrollBar != null)
            ScrollBarSync.ApplyHorizontal(_hScrollBar, _content.HorizontalScale, 0f);
    }

    public void Dispose()
    {
        _content.VerticalScrollPositionChanged -= OnContentVerticalScroll;
        _vScrollBar.ScrollPositionChanged -= _content.SetVerticalNormalizedScrollPosition;

        if (_hScrollBar != null)
        {
            _content.HorizontalScrollPositionChanged -= OnContentHorizontalScroll;
            _hScrollBar.ScrollPositionChanged -= _content.SetHorizontalNormalizedScrollPosition;
        }
    }

    private void OnContentVerticalScroll(float normalized)
        => ScrollBarSync.ApplyVertical(_vScrollBar, _content.VerticalScale, normalized);

    private void OnContentHorizontalScroll(float normalized)
        => ScrollBarSync.ApplyHorizontal(_hScrollBar!, _content.HorizontalScale, normalized);
}
