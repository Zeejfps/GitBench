using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.KeyboardModule;

namespace GitBench;

/// <summary>
/// Window-level keyboard for the pop-out diff window, which has no file list (and therefore no
/// <see cref="ListArrowKbmController"/>) to carry the full-file toggle. Handles the "F" hotkey by
/// bubbling — so hovering the diff body and pressing F works — and steals focus on any click in
/// the window so F keeps working wherever the cursor later rests. It never consumes mouse input,
/// so the diff body's own controllers (hunk buttons, scroll) still receive every click.
/// </summary>
internal sealed class DiffWindowKeyController : KeyboardMouseController
{
    private readonly View _view;
    private readonly Action _onToggleFullFile;

    public DiffWindowKeyController(View view, Action onToggleFullFile)
    {
        _view = view;
        _onToggleFullFile = onToggleFullFile;
    }

    public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        if (e.State != InputState.Pressed) return;
        if (e.Key != KeyboardKey.F) return;
        _onToggleFullFile();
        e.Consume();
    }

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        if (e.State != InputState.Pressed) return;
        if (!_view.Position.ContainsPoint(e.Mouse.Point)) return;
        // Latch keyboard focus to this window's content without consuming the click.
        _view.Context?.Get<InputSystem>()?.StealFocus(this);
    }
}
