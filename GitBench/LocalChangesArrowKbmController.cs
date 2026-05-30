using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.KeyboardModule;

namespace GitGui;

/// <summary>
/// Arrow-key navigation for the local-changes file lists. Lives on
/// <see cref="LocalChangesContentView"/> and takes focus when a row is clicked so Up
/// and Down move the selection within the active side. Shift extends the range from
/// the anchor. Releases focus on a click outside the content view so text inputs and
/// other controllers can claim it.
/// </summary>
internal sealed class LocalChangesArrowKbmController : KeyboardMouseController
{
    private readonly View _view;
    private readonly Action<int, bool> _onMove;
    private readonly Action<bool> _onExpand;
    private readonly Action _onActivate;
    private readonly Action _onDelete;

    // Focus-traversal hooks; wired into the local-changes focus ring so Tab leaves the
    // file list for the commit fields.
    public Action? OnTab { get; set; }
    public Action? OnShiftTab { get; set; }

    public LocalChangesArrowKbmController(
        View view,
        Action<int, bool> onMove,
        Action<bool> onExpand,
        Action onActivate,
        Action onDelete)
    {
        _view = view;
        _onMove = onMove;
        _onExpand = onExpand;
        _onActivate = onActivate;
        _onDelete = onDelete;
    }

    public void TakeFocus()
        => _view.Context?.Get<InputSystem>()?.StealFocus(this);

    public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        if (e.State != InputState.Pressed) return;

        var shift = (e.Modifiers & InputModifiers.Shift) != 0;
        if (e.Key == KeyboardKey.Tab && (OnTab != null || OnShiftTab != null))
        {
            if (shift) OnShiftTab?.Invoke();
            else OnTab?.Invoke();
            e.Consume();
        }
        else if (e.Key == KeyboardKey.UpArrow)
        {
            _onMove(-1, shift);
            e.Consume();
        }
        else if (e.Key == KeyboardKey.DownArrow)
        {
            _onMove(+1, shift);
            e.Consume();
        }
        else if (e.Key == KeyboardKey.RightArrow)
        {
            _onExpand(true);
            e.Consume();
        }
        else if (e.Key == KeyboardKey.LeftArrow)
        {
            _onExpand(false);
            e.Consume();
        }
        else if (e.Key is KeyboardKey.Enter or KeyboardKey.NumpadEnter)
        {
            _onActivate();
            e.Consume();
        }
        else if (e.Key == KeyboardKey.Delete)
        {
            _onDelete();
            e.Consume();
        }
    }

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        if (e.State != InputState.Pressed) return;
        if (_view.Position.ContainsPoint(e.Mouse.Point)) return;
        _view.Context?.Get<InputSystem>()?.Blur(this);
    }
}
