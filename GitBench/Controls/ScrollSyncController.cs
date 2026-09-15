using ZGF.Gui;
using ZGF.Gui.Desktop.Components.HorizontalScrollBar;
using ZGF.Gui.VerticalScrollBar;

namespace GitBench.Controls;

internal sealed class ScrollSyncController : IDisposable
{
    private readonly Action _unsubscribe;

    public ScrollSyncController(
        IScrollableContent content,
        VerticalScrollBar vScrollBar,
        HorizontalScrollBarView? hScrollBar = null)
    {
        void OnVertical(float normalized) => ScrollBarSync.ApplyVertical(vScrollBar, content.VerticalScale, normalized);
        content.VerticalScrollPositionChanged += OnVertical;
        vScrollBar.ScrollPositionChanged += content.SetVerticalNormalizedScrollPosition;
        // Pull the content's current scale so the bar reflects "fits / hidden" state
        // even when no event has fired yet. Critical for views that detach + re-attach
        // (e.g. LocalChangesPanel inside a placeholder-swap parent): each re-attach
        // builds a fresh controller, and without this initial pull the bar would sit
        // at its built-in default (PreferredHeight=12, Scale=0.5) until something
        // unrelated triggered an event.
        OnVertical(0f);
        _unsubscribe = () =>
        {
            content.VerticalScrollPositionChanged -= OnVertical;
            vScrollBar.ScrollPositionChanged -= content.SetVerticalNormalizedScrollPosition;
        };

        if (hScrollBar is not { } hBar) return;
        void OnHorizontal(float normalized) => ScrollBarSync.ApplyHorizontal(hBar, content.HorizontalScale, normalized);
        content.HorizontalScrollPositionChanged += OnHorizontal;
        hBar.ScrollPositionChanged += content.SetHorizontalNormalizedScrollPosition;
        OnHorizontal(0f);
        _unsubscribe += () =>
        {
            content.HorizontalScrollPositionChanged -= OnHorizontal;
            hBar.ScrollPositionChanged -= content.SetHorizontalNormalizedScrollPosition;
        };
    }

    public ScrollSyncController(VerticalScrollPane pane, VerticalScrollBar scrollBar)
    {
        void OnPane(float normalized) => ScrollBarSync.ApplyVertical(scrollBar, pane.Scale, normalized);
        void OnBar(float normalized) => pane.SetNormalizedScrollPosition(normalized, notify: false);
        pane.ScrollPositionChanged += OnPane;
        scrollBar.ScrollPositionChanged += OnBar;
        _unsubscribe = () =>
        {
            pane.ScrollPositionChanged -= OnPane;
            scrollBar.ScrollPositionChanged -= OnBar;
        };
    }

    public void Dispose() => _unsubscribe();
}
