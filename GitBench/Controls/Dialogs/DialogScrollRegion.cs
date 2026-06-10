using GitBench.Features.Operations;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.VerticalScrollBar;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.VerticalScrollBar;
using ZGF.Gui.Views;

namespace GitBench.Controls.Dialogs;

/// <summary>
/// Wraps dialog body content in a vertical scroll viewport whose scrollbar appears only when the
/// content can't fit the height it's given. When <see cref="CenterView"/> caps a dialog to the
/// window, the frame hands this region less height than the content wants and it scrolls instead
/// of overflowing; when the body fits, the viewport is the content's natural height, the bar stays
/// hidden, and the dialog looks exactly as it did before. This is the single place every dialog
/// gets "never taller than the window" without each one wiring its own scroll machinery.
/// </summary>
internal sealed class DialogScrollRegion : MultiChildView
{
    private readonly VerticalScrollPane _pane;
    private readonly VerticalScrollBarView _bar;

    public DialogScrollRegion(View content)
    {
        _pane = new VerticalScrollPane();
        _pane.Children.Add(content);
        _pane.UseController(_ => new VerticalScrollPaneWheelController(_pane));

        _bar = ScrollBars.CreateVertical();
        // FlexRowView skips invisible children, so the bar costs no horizontal gutter until the
        // content actually overflows. Scale < 1 means the viewport is smaller than the content —
        // i.e. there is somewhere to scroll to.
        _bar.IsVisible = false;
        _pane.ScrollPositionChanged += _ => _bar.IsVisible = _pane.Scale < 1f;
        this.Use(_ => new VerticalScrollBarSyncController(_pane, _bar));

        AddChildToSelf(new FlexRowView
        {
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                new FlexItem { Grow = 1, Child = _pane },
                _bar,
            },
        });
    }
}
