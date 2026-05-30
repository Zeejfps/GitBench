using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Gui.Views;

namespace GitGui;

/// <summary>
/// Handles only the column-divider hover/drag. Row wheel/click/right-click are owned
/// by the <see cref="VirtualRowListView"/> child and its controller. On press in the
/// capture phase this controller checks for a divider hit first: if it lands, it
/// consumes the event and the widget never sees it; otherwise it lets the press
/// bubble to the widget for normal row handling.
/// </summary>
internal sealed class CommitsViewController : KeyboardMouseController
{
    private readonly CommitsView _view;
    private CommitsView.DividerKind _activeDivider = CommitsView.DividerKind.None;
    private float _lastDragX;

    public CommitsViewController(CommitsView view, Context context)
    {
        _view = view;
        _ = context;
    }

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Button != MouseButton.Left) return;

        if (e.State == InputState.Pressed)
        {
            // Only act in capture phase — that's when we want to intercept before the
            // widget gets the click. If we fired again in bubble, we'd re-hit-test and
            // potentially start a second drag with the same press.
            if (e.Phase != EventPhase.Capturing) return;

            var divider = _view.HitTestDivider(e.Mouse.Point);
            if (divider == CommitsView.DividerKind.None) return;

            _activeDivider = divider;
            _lastDragX = e.Mouse.Point.X;
            _view.Context.StealFocus(this);
            e.Consume();
            return;
        }

        if (e.State == InputState.Released && _activeDivider != CommitsView.DividerKind.None)
        {
            _activeDivider = CommitsView.DividerKind.None;
            _view.Context.Blur(this);
            e.Consume();
        }
    }

    public override void OnMouseMoved(ref MouseMoveEvent e)
    {
        if (_activeDivider != CommitsView.DividerKind.None)
        {
            var dx = e.Mouse.Point.X - _lastDragX;
            _lastDragX = e.Mouse.Point.X;
            switch (_activeDivider)
            {
                case CommitsView.DividerKind.Author:
                    _view.ResizeAuthorColumn(dx);
                    break;
                case CommitsView.DividerKind.Hash:
                    _view.ResizeHashColumn(dx);
                    break;
                case CommitsView.DividerKind.Date:
                    _view.ResizeDateColumn(dx);
                    break;
            }
            e.Consume();
            return;
        }
        _view.SetHoveredDivider(_view.HitTestDivider(e.Mouse.Point));
    }

    public override void OnMouseExit(ref MouseExitEvent e)
    {
        if (_activeDivider == CommitsView.DividerKind.None)
        {
            _view.SetHoveredDivider(CommitsView.DividerKind.None);
        }
    }
}
